using System.Globalization;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.OwnerConsole;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// One process-lifetime observation window; only a new home adoption changes its fleet.
internal sealed class FleetBuildAdoptionSummary : IDisposable
{
    private readonly OrchestratorProjectRegistry _registry;
    private readonly Action<string> _append;
    private readonly TimeProvider _time;
    private readonly TimeSpan _deadline;
    private readonly TimeSpan _pollInterval;
    private readonly IConductLockPidProbe? _pidProbe;
    private readonly string? _homeRoot;
    private readonly Action<string, string, string, string>? _onFinalized;
    private readonly object _gate = new();
    private readonly HashSet<string> _finalized = new(StringComparer.OrdinalIgnoreCase);
    private Observation? _pending;
    private ITimer? _timer;
    private bool _disposed;

    internal FleetBuildAdoptionSummary(
        OrchestratorProjectRegistry registry, Action<string> append, TimeProvider time,
        TimeSpan? deadline = null, IConductLockPidProbe? pidProbe = null,
        string? homeRoot = null, TimeSpan? pollInterval = null,
        Action<string, string, string, string>? onFinalized = null)
    {
        _registry = registry;
        _append = append;
        _time = time;
        _onFinalized = onFinalized;
        _deadline = deadline ?? TimeSpan.FromMinutes(15);
        _pollInterval = pollInterval ?? TimeSpan.FromSeconds(5);
        if (_deadline < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(deadline));
        if (_pollInterval != Timeout.InfiniteTimeSpan && _pollInterval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(pollInterval));
        _pidProbe = pidProbe;
        _homeRoot = homeRoot is null ? null : Path.TrimEndingDirectorySeparator(Path.GetFullPath(homeRoot));
    }

    internal void OnHomeAdopted(string commit, DateTimeOffset? adoptedAt = null)
    {
        if (!HomeBuildAdoptionDetector.IsCommit(commit)) return;
        var completed = new List<(string Commit, string Pending, string Failed, string Stopped)>();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_finalized.Contains(commit) ||
                string.Equals(_pending?.Commit, commit, StringComparison.OrdinalIgnoreCase)) return;

            var now = _time.GetUtcNow();
            if (_pending is not null) FinalizePending(now, completed);
            // A registry read failure is surfaced by the activation caller, never an empty-fleet success.
            var expected = _registry.ListProjects()
                .Where(project => !string.Equals(
                    Path.TrimEndingDirectorySeparator(Path.GetFullPath(project.RootDirectory)),
                    _homeRoot, StringComparison.OrdinalIgnoreCase))
                .Select(project => (Project: project, Workspace: project.ResolveWorkspace()))
                .Where(entry => IsRunning(entry.Workspace))
                .OrderBy(entry => entry.Project.Name, StringComparer.Ordinal)
                .Select(entry => new ExpectedProject(entry.Project.Name, entry.Workspace.ConductEventsLogPath,
                    entry.Workspace.OrchestratorDirectory))
                .ToArray();
            _pending = new Observation(commit, adoptedAt ?? now, expected);
            Evaluate(now, completed);
            if (_pending is not null && _pollInterval != Timeout.InfiniteTimeSpan)
            {
                _timer = _time.CreateTimer(_ => Tick(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                _timer.Change(_pollInterval, _pollInterval);
            }
        }
        NotifyFinalized(completed);
    }

    internal void Tick()
    {
        // Timer callbacks have no activation caller to catch observation or reporting failures.
        AdoptedBuildPublicationReporting.Try(TickCore, "fleet-summary-poll");
    }

    private void TickCore()
    {
        var completed = new List<(string Commit, string Pending, string Failed, string Stopped)>();
        lock (_gate)
        {
            if (!_disposed && _pending is not null) Evaluate(_time.GetUtcNow(), completed);
        }
        NotifyFinalized(completed);
    }

    private void NotifyFinalized(List<(string Commit, string Pending, string Failed, string Stopped)> completed)
    {
        foreach (var item in completed)
            AdoptedBuildPublicationReporting.Try(
                () => _onFinalized?.Invoke(item.Commit, item.Pending, item.Failed, item.Stopped), "fleet-finalized-callback");
    }

    private bool IsRunning(OrchestratorWorkspace workspace)
    {
        try { return ConductorLoopLease.IsActive(workspace.OrchestratorDirectory, _pidProbe); }
        catch (InvalidOperationException) { return false; } // Malformed lock, not a live owner.
    }

    private void Evaluate(DateTimeOffset now, List<(string Commit, string Pending, string Failed, string Stopped)> completed)
    {
        var states = Observe(_pending!, now);
        if (states.All(state => state is ProjectState.Adopted or ProjectState.Stopped) || now - _pending!.StartedAt >= _deadline)
            FinalizePending(now, completed, states);
    }

    private void FinalizePending(DateTimeOffset now,
        List<(string Commit, string Pending, string Failed, string Stopped)> completed, ProjectState[]? states = null)
    {
        var observation = _pending!;
        states ??= Observe(observation, now);
        _pending = null;
        _timer?.Dispose();
        _timer = null;
        if (!_finalized.Add(observation.Commit)) return;

        string Names(ProjectState state) => string.Join(",", observation.Projects
            .Where((_, index) => states[index] == state).Select(project => project.Name));
        var elapsed = Math.Max(0, Math.Floor((now - observation.StartedAt).TotalSeconds))
            .ToString(CultureInfo.InvariantCulture);
        if (AdoptedBuildPublicationReporting.Try(
            () => _append($"FLEET_BUILD_ADOPTED commit={observation.Commit} adopted={Names(ProjectState.Adopted)} " +
                $"pending={Names(ProjectState.Pending)} failed={Names(ProjectState.Failed)} " +
                $"stopped={Names(ProjectState.Stopped)} elapsedSeconds={elapsed}"), "fleet-summary-event"))
            completed.Add((observation.Commit, Names(ProjectState.Pending), Names(ProjectState.Failed), Names(ProjectState.Stopped)));
    }

    private ProjectState[] Observe(Observation observation, DateTimeOffset now) =>
        observation.Projects.Select(project =>
        {
            var state = ReadState(project.LogPath, observation, now);
            // Only positive inactivity evidence resolves a project with no commit-specific outcome.
            return state == ProjectState.Pending &&
                ConductorLoopLease.ProbeLiveness(project.OrchestratorDirectory, _pidProbe) == ConductorLiveness.NotRunning
                ? ProjectState.Stopped : state;
        }).ToArray();

    private static ProjectState ReadState(string logPath, Observation observation, DateTimeOffset now)
    {
        var state = ProjectState.Pending;
        var latest = DateTimeOffset.MinValue;
        try
        {
            var directory = Path.GetDirectoryName(logPath)!;
            if (!Directory.Exists(directory)) return state;
            // Read oldest generations first and the active file last, including rotation during a wait.
            var paths = Directory.GetFiles(directory, "conduct-events-*.log")
                .OrderBy(File.GetLastWriteTimeUtc).ThenBy(path => path, StringComparer.Ordinal)
                .Append(logPath);
            foreach (var path in paths)
            {
                try
                {
                    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete);
                    using var reader = new StreamReader(stream);
                    while (reader.ReadLine() is { } line)
                    {
                        if (TryReadState(line, observation, now, out var at, out var candidate) && at >= latest)
                        {
                            latest = at;
                            state = candidate;
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Missing, rotating or temporarily locked logs remain unresolved; retry next tick.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return state;
    }

    private static bool TryReadState(string line, Observation observation, DateTimeOffset now,
        out DateTimeOffset at, out ProjectState state)
    {
        at = default;
        state = ProjectState.Pending;
        try
        {
            using var json = JsonDocument.Parse(line);
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("timestamp", out var timestamp) || timestamp.ValueKind != JsonValueKind.String ||
                !timestamp.TryGetDateTimeOffset(out at) || at < observation.StartedAt || at > now ||
                !root.TryGetProperty("detail", out var detail) || detail.ValueKind != JsonValueKind.String)
                return false;
            var fields = detail.GetString()!.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length == 0) return false;
            state = fields[0] switch
            {
                "ACTIVATION_ADOPTED" => ProjectState.Adopted,
                "ACTIVATION_REVERTED" or "LOOP_HANDOFF_FAILED" => ProjectState.Failed,
                _ => ProjectState.Pending
            };
            if (state == ProjectState.Pending) return false;
            foreach (var field in fields.Skip(1))
            {
                var separator = field.IndexOf('=');
                if (separator < 0) continue;
                var key = field[..separator];
                var matchesKey = fields[0] switch
                {
                    "ACTIVATION_ADOPTED" => key == "adoptedCommit",
                    "ACTIVATION_REVERTED" => key == "failedCommit",
                    _ => key is "commit" or "failedCommit" or "targetCommit" or "candidateCommit"
                };
                if (matchesKey && string.Equals(field[(separator + 1)..], observation.Commit,
                    StringComparison.OrdinalIgnoreCase)) return true;
            }
        }
        catch (JsonException) { } // Ignore partial/malformed records without inventing failure evidence.
        return false;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _timer?.Dispose();
            _timer = null;
            _pending = null;
        }
    }

    private enum ProjectState { Pending, Adopted, Failed, Stopped }
    private sealed record ExpectedProject(string Name, string LogPath, string OrchestratorDirectory);
    private sealed record Observation(string Commit, DateTimeOffset StartedAt, ExpectedProject[] Projects);
}
