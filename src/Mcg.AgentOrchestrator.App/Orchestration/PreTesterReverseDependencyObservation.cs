using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Schema 2 seals completed ticks rather than equating normal loop exit with completion.
internal sealed class PreTesterReverseDependencyObservation : IDisposable
{
    internal const int Schema = 2;
    internal static readonly TimeSpan Duration = TimeSpan.FromHours(24);
    private readonly StreamWriter _writer;
    private readonly Dictionary<string, Workload> _mix = new(StringComparer.Ordinal);
    private int _entered;
    private int _sessionWritten;
    private int _written;
    private int _completedTicks;
    private readonly Producer _producer;

    internal PreTesterReverseDependencyObservation(string path, Guid id, DateTimeOffset start, Func<Producer> identify)
    {
        Path = path;
        _writer = new StreamWriter(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read));
        try
        {
            _producer = identify();
            Header = new(Schema, id, start, start + Duration, _producer);
            Write(Header);
            _writer.Flush();
        }
        catch
        {
            _writer.Dispose();
            throw;
        }
    }

    internal PreTesterReverseDependencyObservation(string path, Guid id, DateTimeOffset now,
        Func<Producer> identify, string handoff)
    {
        Path = path;
        var parts = handoff.Split(':');
        if (parts.Length != 3 || !Guid.TryParseExact(parts[0], "N", out var windowId) || windowId != id ||
            !Guid.TryParseExact(parts[1], "N", out var token))
            throw new InvalidDataException("Resolver observation continuation has the wrong window or token.");
        var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        try
        {
            if (!Convert.ToHexString(SHA256.HashData(stream)).Equals(parts[2], StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Resolver observation continuation digest mismatch.");
            stream.Position = 0;
            using var input = new StreamReader(stream, leaveOpen: true);
            Header = JsonSerializer.Deserialize<WindowHeader>(input.ReadLine() ?? "") ??
                throw new InvalidDataException("Resolver observation continuation has no header.");
            string? last = null;
            while (input.ReadLine() is { } line) last = line;
            var checkpoint = JsonSerializer.Deserialize<SessionCheckpoint>(last ?? "") ??
                throw new InvalidDataException("Resolver observation continuation has no checkpoint.");
            if (Header.Version != Schema || Header.WindowId != id || Header.Deadline - Header.Started != Duration ||
                checkpoint.Token != token || checkpoint.SessionId == Guid.Empty ||
                checkpoint.SuspendedAt < Header.Started || checkpoint.SuspendedAt >= Header.Deadline ||
                checkpoint.SuspendedAt > now || checkpoint.CompletedTicks < 1 || checkpoint.InvocationCount < 0 ||
                checkpoint.Workloads is null)
                throw new InvalidDataException("Invalid resolver observation continuation checkpoint.");

            _producer = identify();
            foreach (var workload in checkpoint.Workloads)
                _mix.Add(WorkloadKey(workload.Project, workload.Names), workload);
            _written = checkpoint.InvocationCount;
            _completedTicks = checkpoint.CompletedTicks;
            LastCompletedAt = checkpoint.SuspendedAt;
            _writer = new StreamWriter(stream);
            Write(new SessionStart(_producer, checkpoint.SessionId, checkpoint.Token, now));
            _writer.Flush();
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    internal string Path { get; }
    internal WindowHeader Header { get; }
    internal DateTimeOffset LastCompletedAt { get; private set; }

    // Count boundary entries independently of successful row capture.
    internal int Enter() => Interlocked.Increment(ref _entered);

    private void Commit(Tick tick, DateTimeOffset end)
    {
        var boundaryEntries = Volatile.Read(ref _entered) - _sessionWritten;
        var rows = tick.ReadEntries(_sessionWritten, boundaryEntries);
        if (end < tick.Started || (_completedTicks != 0 && tick.Started < LastCompletedAt))
            throw new InvalidDataException("Resolver observation clock regressed.");
        if (end > Header.Deadline)
            return;

        foreach (var row in rows)
        {
            var key = WorkloadKey(row.Project, row.Names);
            var count = _mix.TryGetValue(key, out var workload) ? workload.Count + 1 : 1;
            _mix[key] = new(row.Project, row.Names, count);
        }
        Write(new CompletedTick(++_completedTicks, _producer.SessionId, tick.Started, end, boundaryEntries, rows));
        _writer.Flush();
        _written += boundaryEntries;
        _sessionWritten += boundaryEntries;
        LastCompletedAt = end;
    }

    internal Seal Complete(DateTimeOffset sealedAt)
    {
        if (_completedTicks == 0 || sealedAt < Header.Deadline)
            throw new InvalidDataException("Resolver observation has not completed its window.");
        var mix = _mix.Values.ToArray();
        var coverage = mix.Count(workload => workload.Names.Distinct(StringComparer.Ordinal).Take(2).Count() == 2) >= 2;
        var manifest = new Manifest(_completedTicks, _written, mix, coverage, sealedAt);
        Write(manifest);
        _writer.Dispose();
        return new(Schema, Header.WindowId, _producer.SessionId, "SHA-256", Sha256(Path),
            coverage ? "complete" : "insufficient-coverage", null);
    }

    internal string Suspend(DateTimeOffset now)
    {
        if (_completedTicks == 0 || now < LastCompletedAt || now >= Header.Deadline)
            throw new InvalidDataException("Resolver observation cannot checkpoint this session.");
        var token = Guid.NewGuid();
        Write(new SessionCheckpoint(_producer.SessionId, token, now, _completedTicks, _written, _mix.Values.ToArray()));
        _writer.Dispose();
        return $"{Header.WindowId:N}:{token:N}:{Sha256(Path)}";
    }

    private void Write<T>(T record) => _writer.WriteLine(JsonSerializer.Serialize(record));

    public void Dispose() => _writer.Dispose();

    internal static string Sha256(string path)
    {
        using var input = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(input));
    }

    internal static Producer CurrentProducer(OrchestratorWorkspace workspace, Guid sessionId)
    {
        using var process = Process.GetCurrentProcess();
        var started = new DateTimeOffset(process.StartTime.ToUniversalTime());
        var owner = new SystemConductorLockProbe().ActiveOwnership(workspace);
        if (owner?.Pid != process.Id || owner.ProcessStartedAt != started || owner.AcquiredAt is null)
            throw new InvalidDataException("Resolver observation requires the current conductor lease.");
        var app = typeof(PreTesterReverseDependencyObservation).Assembly.Location;
        var revision = File.ReadAllText(app + ".git-head").Trim();
        if (!IsRevision(revision))
            throw new InvalidDataException("Resolver observation requires a staged App revision.");
        return new(sessionId, revision, Sha256(app), process.Id, started, workspace.OrchestratorDirectory, owner.AcquiredAt);
    }

    internal static bool IsRevision(string value) => value is { Length: 40 } && value.All(Uri.IsHexDigit);

    internal static string WorkloadKey(string project, string[] names) =>
        JsonSerializer.Serialize(new { Project = project, Names = names });

    internal static bool IsObservationFailure(Exception error) =>
        error is IOException or UnauthorizedAccessException or InvalidDataException or System.Security.SecurityException
            or ArgumentException or InvalidOperationException or NotSupportedException or JsonException;

    internal sealed class Tick(PreTesterReverseDependencyObservation writer, DateTimeOffset started,
        Action<Tick, bool> finish) : IDisposable
    {
        private readonly object _gate = new();
        private readonly Dictionary<int, Invocation> _rows = [];
        private bool _finished;
        private bool _failed;
        internal DateTimeOffset Started { get; } = started;

        internal int? Enter()
        {
            lock (_gate)
                return _finished ? null : writer.Enter();
        }

        internal void Record(int ordinal, Boundary boundary, string project, IReadOnlyList<string> names,
            IReadOnlyList<AcceptanceTestClassDescriptor> inventory, string[] paths, string? unresolvedName,
            ReverseDependencyEvidenceCostRule.Decision? cost)
        {
            lock (_gate)
            {
                if (_finished) return;
                try
                {
                    _rows.Add(ordinal, new(ordinal, boundary, project, names.ToArray(),
                        inventory.Select(item => item.FullName).ToArray(), paths.ToArray(), unresolvedName, cost));
                }
                catch (Exception error) when (IsObservationFailure(error))
                {
                    _failed = true;
                }
            }
        }

        internal Invocation[] ReadEntries(int priorCount, int count)
        {
            if (_failed || _rows.Count != count)
                throw new InvalidDataException("Resolver observation did not capture every boundary entry.");
            var rows = new Invocation[count];
            for (var index = 0; index < rows.Length; index++)
            {
                if (!_rows.TryGetValue(priorCount + index + 1, out var row))
                    throw new InvalidDataException("Resolver observation entry ordinals are not contiguous.");
                rows[index] = row;
            }
            return rows;
        }

        internal void Commit(DateTimeOffset end) => writer.Commit(this, end);

        internal void Complete() => Finish(completed: true);

        public void Dispose() => Finish(completed: false);

        private void Finish(bool completed)
        {
            lock (_gate)
            {
                if (_finished) return;
                _finished = true;
                finish(this, completed);
            }
        }
    }

    internal sealed record Producer(Guid SessionId, string Revision, string AppSha256, int ProcessId,
        DateTimeOffset ProcessStartedAt, string AuthorityDirectory, DateTimeOffset? LeaseAcquiredAt = null);

    internal sealed record WindowHeader(int Version, Guid WindowId, DateTimeOffset Started, DateTimeOffset Deadline,
        Producer Producer);

    internal sealed record Boundary(string GoalId, string TesterTaskId, string GoalCandidateRevision);

    internal sealed record Invocation(int Ordinal, Boundary Boundary, string Project, string[] Names, string[] Inventory,
        string[] Paths, string? UnresolvedName, ReverseDependencyEvidenceCostRule.Decision? Cost);

    internal sealed record SessionCheckpoint(Guid SessionId, Guid Token, DateTimeOffset SuspendedAt, int CompletedTicks,
        int InvocationCount, Workload[] Workloads);

    internal sealed record SessionStart(Producer Producer, Guid PreviousSessionId, Guid Token, DateTimeOffset Started);

    internal sealed record CompletedTick(int Number, Guid SessionId, DateTimeOffset Started, DateTimeOffset Ended,
        int BoundaryEntries, Invocation[] Rows);

    internal sealed record Workload(string Project, string[] Names, int Count);

    internal sealed record Manifest(int CompleteTickCount, int InvocationCount, Workload[] Workloads,
        bool HasCpuCoverage, DateTimeOffset SealedAt);

    internal sealed record Seal(int Version, Guid WindowId, Guid SessionId, string Algorithm, string? Sha256,
        string Status, string? Reason);
}
