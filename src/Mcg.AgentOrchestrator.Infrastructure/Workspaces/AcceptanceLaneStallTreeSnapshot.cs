using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Mcg.AgentOrchestrator.Infrastructure;

// A bounded diagnostic of one owned tree; inspection failures never prevent its stop.
internal sealed record AcceptanceLaneStallTreeSnapshot(
    int RootPid, DateTimeOffset CapturedAt, double IntervalSeconds,
    IReadOnlyList<AcceptanceLaneStallTreeSnapshot.Entry> Processes,
    int OmittedProcesses = 0, string? Truncated = null, string? Unavailable = null)
{
    internal const int ProcessCap = 64;
    internal const int SizeCapBytes = 32768;
    private const int ThreadCap = 64;
    private static readonly TimeSpan SampleInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan CaptureDeadline = TimeSpan.FromSeconds(15);
    private static readonly AsyncLocal<Inputs?> TestInputs = new();

    internal sealed record ThreadEntry(int Id, string State, string? WaitReason, string? Unreadable = null);
    internal sealed record Reading(double CpuSeconds, int ThreadCount,
        IReadOnlyList<ThreadEntry> Threads, int OmittedThreads = 0);
    internal sealed record Entry(int Pid, int ParentPid, string Image, DateTimeOffset? StartedAt,
        string? CommandLine, double? CpuSecondsFirst, double? CpuSecondsSecond,
        int? ThreadCount, IReadOnlyList<ThreadEntry> Threads, int OmittedThreads, string? Unreadable)
    {
        public IReadOnlyDictionary<string, int>? ThreadStates { get; init; }
    }
    internal sealed record Inputs(
        Func<int, IReadOnlyList<ProcessInspectionRecord>>? Walk = null,
        Func<ProcessInspectionRecord, Reading>? Read = null,
        Func<TimeSpan, CancellationToken, Task>? Delay = null,
        TimeSpan? Interval = null, TimeSpan? Deadline = null);

    internal static IDisposable PushInputsForTests(Inputs inputs)
    {
        var previous = TestInputs.Value;
        TestInputs.Value = inputs;
        return new RestoreScope(() => TestInputs.Value = previous);
    }

    internal static async Task<AcceptanceLaneStallTreeSnapshot> CaptureAsync(int rootPid,
        CancellationToken cancellationToken = default)
    {
        var inputs = TestInputs.Value ?? new Inputs();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var stopToken = stop.Token;
        try
        {
            // Native inspection may block. Bound the caller independently of the inspection work.
            var capture = Task.Run(() => CaptureCoreAsync(rootPid, inputs, stopToken));
            return await capture.WaitAsync(inputs.Deadline ?? CaptureDeadline).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            stop.Cancel();
            return new(rootPid, DateTimeOffset.UtcNow, 0, [], Unavailable: "deadline");
        }
        catch (Exception ex)
        {
            return new(rootPid, DateTimeOffset.UtcNow, 0, [], Unavailable: ex.GetType().Name);
        }
    }

    private static async Task<AcceptanceLaneStallTreeSnapshot> CaptureCoreAsync(int rootPid, Inputs inputs,
        CancellationToken stop)
    {
        try
        {
            var records = (inputs.Walk ?? Walk)(rootPid);
            var entries = new List<Entry>();
            var selected = records.Take(ProcessCap).ToArray();
            var capturedAt = DateTimeOffset.UtcNow;
            var interval = Stopwatch.StartNew();
            foreach (var record in selected)
            {
                stop.ThrowIfCancellationRequested();
                Reading? reading = null;
                string? unreadable = null;
                try { reading = (inputs.Read ?? ReadProcess)(record); }
                catch (Exception ex) { unreadable = ex.GetType().Name + ":" + Bound(ex.Message, 128); }
                entries.Add(new(record.ProcessId, record.ParentProcessId, Bound(record.Name, 256)!,
                    record.StartedAt, Bound(record.CommandLine, 1024), reading?.CpuSeconds, null,
                    reading?.ThreadCount, reading?.Threads.Take(ThreadCap).ToArray() ?? [],
                    (reading?.OmittedThreads ?? 0) + Math.Max(0, (reading?.Threads.Count ?? 0) - ThreadCap), unreadable));
            }

            var cancelled = false;
            try { await (inputs.Delay ?? Task.Delay)(inputs.Interval ?? SampleInterval, stop).ConfigureAwait(false); }
            catch (OperationCanceledException) { cancelled = true; }
            var intervalSeconds = interval.Elapsed.TotalSeconds;
            for (var index = 0; index < selected.Length; index++)
            {
                if (cancelled || stop.IsCancellationRequested) break;
                try
                {
                    var second = (inputs.Read ?? ReadProcess)(selected[index]);
                    entries[index] = entries[index] with { CpuSecondsSecond = second.CpuSeconds };
                }
                catch (Exception ex)
                {
                    entries[index] = entries[index] with { Unreadable = ex.GetType().Name + ":" + Bound(ex.Message, 128) };
                }
            }
            var omitted = Math.Max(0, records.Count - selected.Length);
            return new(rootPid, capturedAt, intervalSeconds, entries, omitted,
                omitted > 0 ? "process-cap" : null, cancelled ? "cancelled-interval" : null);
        }
        catch (Exception ex)
        {
            return new(rootPid, DateTimeOffset.UtcNow, 0, [], Unavailable: ex.GetType().Name + ":" + Bound(ex.Message, 128));
        }
    }

    private static IReadOnlyList<ProcessInspectionRecord> Walk(int rootPid)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("not-windows");
        var tree = ProcessCommandLines.Snapshot(new ProcessInspectionQuery(
            new HashSet<int> { rootPid }, new HashSet<int>(), new HashSet<string>(), true, false, new HashSet<int>()));
        if (tree.Failure is { } failure) throw new InvalidOperationException(failure.Operation);
        if (!tree.Records.TryGetValue(rootPid, out var root)) throw new InvalidOperationException("root-unavailable");
        var children = tree.Records.Values.ToLookup(record => record.ParentProcessId);
        var ordered = new List<ProcessInspectionRecord>();
        var queue = new Queue<ProcessInspectionRecord>();
        var seen = new HashSet<int>();
        queue.Enqueue(root);
        while (queue.TryDequeue(out var record))
        {
            if (!seen.Add(record.ProcessId)) continue;
            ordered.Add(record);
            foreach (var child in children[record.ProcessId]) queue.Enqueue(child);
        }
        return ordered;
    }

    internal static Reading ReadProcess(ProcessInspectionRecord record)
    {
        if (record.StartedAt is null) throw new InvalidOperationException("identity-unavailable:" + record.Status);
        using var process = Process.GetProcessById(record.ProcessId);
        if (new DateTimeOffset(process.StartTime.ToUniversalTime()) != record.StartedAt)
            throw new InvalidOperationException("pid-reused");
        var cpu = process.TotalProcessorTime.TotalSeconds;
        var threads = process.Threads;
        var details = new List<ThreadEntry>();
        try
        {
            for (var index = 0; index < Math.Min(threads.Count, ThreadCap); index++)
            {
                var thread = threads[index];
                try
                {
                    var state = thread.ThreadState;
                    details.Add(new(thread.Id, state.ToString(),
                        OperatingSystem.IsWindows() && state == System.Diagnostics.ThreadState.Wait
                            ? thread.WaitReason.ToString() : null));
                }
                catch (Exception ex) { details.Add(new(0, "unreadable", null, ex.GetType().Name)); }
            }
            return new(cpu, threads.Count, details, Math.Max(0, threads.Count - ThreadCap));
        }
        finally
        {
            foreach (ProcessThread thread in threads) thread.Dispose();
        }
    }

    internal string ToLine(string lane)
    {
        var prefix = "LANE_STALL_TREE ";
        var snapshot = this;
        string Encode() => prefix + JsonSerializer.Serialize(new { Lane = Bound(lane, 256), Snapshot = snapshot });
        var line = Encode();
        if (Encoding.UTF8.GetByteCount(line) <= SizeCapBytes) return line;

        // Preserve CPU and identity before thread detail when the byte bound is reached.
        snapshot = snapshot with
        {
            Truncated = Append(snapshot.Truncated, "thread-detail"),
            Processes = snapshot.Processes.Select(entry => entry with
            {
                ThreadStates = entry.Threads.GroupBy(thread => thread.State + ":" + thread.WaitReason + ":" + thread.Unreadable)
                    .ToDictionary(group => group.Key, group => group.Count()),
                Threads = [], OmittedThreads = entry.OmittedThreads + entry.Threads.Count
            }).ToArray()
        };
        line = Encode();
        while (Encoding.UTF8.GetByteCount(line) > SizeCapBytes && snapshot.Processes.Count > 0)
        {
            snapshot = snapshot with
            {
                Truncated = Append(snapshot.Truncated, "size-cap"),
                OmittedProcesses = snapshot.OmittedProcesses + 1,
                Processes = snapshot.Processes.Take(snapshot.Processes.Count - 1).ToArray()
            };
            line = Encode();
        }
        return line;
    }

    private static string Append(string? markers, string marker) =>
        markers?.Contains(marker, StringComparison.Ordinal) == true ? markers : markers is null ? marker : markers + "," + marker;
    private static string? Bound(string? text, int length) => text?.Length > length ? text[..length] : text;
    private sealed class RestoreScope(Action restore) : IDisposable { public void Dispose() => restore(); }
}
