using System.Text;
using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: the process tree, readings and interval are scoped synthetic inputs.
public sealed class AcceptanceLaneStallTreeSnapshotTests
{
    [Fact]
    public async Task Stall_line_names_launched_and_tracked_identities()
    {
        var lines = new List<string>();
        using var monitorInputs = AcceptanceLaneStallMonitor.PushInputsForTests(TimeSpan.Zero, lines.Add);
        using var snapshotInputs = AcceptanceLaneStallTreeSnapshot.PushInputsForTests(new(
            Walk: _ => [Record(1)], Read: _ => new(1, 0, []), Delay: (_, _) => Task.CompletedTask));
        using var monitor = CreateMonitor();
        monitor.Observe(Heartbeat());
        monitor.Observe(Heartbeat());
        var tracked = new SpawnProcessIdentity(2, DateTimeOffset.UnixEpoch.AddSeconds(5), "tracked.exe");

        monitor.EmitTree(await AcceptanceLaneStallTreeSnapshot.CaptureAsync(1), tracked);

        Assert.Equal(2, lines.Count);
        Assert.StartsWith("LANE_STALLED ", lines[0]);
        using var json = Parse(lines[1]);
        var encoded = json.RootElement.GetProperty("Snapshot");
        Assert.Equal(1, encoded.GetProperty("RootPid").GetInt32());
        Assert.Equal(tracked.ProcessId, encoded.GetProperty("TrackedPid").GetInt32());
        Assert.Equal(tracked.StartedAt, encoded.GetProperty("TrackedStartTime").GetDateTimeOffset());
        Assert.Equal(new[] { "RootPid", "TrackedPid", "TrackedStartTime" },
            encoded.EnumerateObject().Take(3).Select(property => property.Name).ToArray());
    }

    [Fact]
    public async Task Stall_line_marks_absent_tracked_identity_within_size_cap()
    {
        var lines = new List<string>();
        var records = Enumerable.Range(1, 6).Select(Record).ToArray();
        using var monitorInputs = AcceptanceLaneStallMonitor.PushInputsForTests(TimeSpan.Zero, lines.Add);
        using var snapshotInputs = AcceptanceLaneStallTreeSnapshot.PushInputsForTests(new(
            Walk: _ => records,
            Read: _ => new(1, 64, Enumerable.Range(1, 64)
                .Select(id => new AcceptanceLaneStallTreeSnapshot.ThreadEntry(id, "Wait", "Executive")).ToArray()),
            Delay: (_, _) => Task.CompletedTask));
        using var monitor = CreateMonitor();
        using var childWait = CancellationTokenSource.CreateLinkedTokenSource(monitor.Token);
        var childStops = 0;
        using var registration = childWait.Token.Register(() => childStops++);
        monitor.Observe(Heartbeat());
        monitor.Observe(Heartbeat());
        Assert.True(childWait.IsCancellationRequested);
        Assert.Equal(1, childStops);

        monitor.EmitTree(await AcceptanceLaneStallTreeSnapshot.CaptureAsync(1), tracked: null);
        monitor.Observe(Heartbeat());

        Assert.Equal(1, childStops);
        Assert.Equal(2, lines.Count);
        Assert.StartsWith("LANE_STALLED ", lines[0]);
        Assert.True(Encoding.UTF8.GetByteCount(lines[1]) <= AcceptanceLaneStallTreeSnapshot.SizeCapBytes);
        using var json = Parse(lines[1]);
        var encoded = json.RootElement.GetProperty("Snapshot");
        Assert.Equal(1, encoded.GetProperty("RootPid").GetInt32());
        Assert.Equal(AcceptanceLaneStallTreeSnapshot.TrackedUnavailable, encoded.GetProperty("TrackedPid").GetString());
        Assert.Equal(AcceptanceLaneStallTreeSnapshot.TrackedUnavailable, encoded.GetProperty("TrackedStartTime").GetString());
        Assert.Equal("thread-detail", encoded.GetProperty("Truncated").GetString());
        Assert.Equal(0, encoded.GetProperty("OmittedProcesses").GetInt32());
        var processes = encoded.GetProperty("Processes");
        Assert.Equal(records.Length, processes.GetArrayLength());
        Assert.Equal(records.Select(record => record.ProcessId),
            processes.EnumerateArray().Select(process => process.GetProperty("Pid").GetInt32()));
        foreach (var process in processes.EnumerateArray())
        {
            Assert.Equal(DateTimeOffset.UnixEpoch, process.GetProperty("StartedAt").GetDateTimeOffset());
            Assert.Equal(1d, process.GetProperty("CpuSecondsFirst").GetDouble());
            Assert.Equal(1d, process.GetProperty("CpuSecondsSecond").GetDouble());
            Assert.Equal(0, process.GetProperty("Threads").GetArrayLength());
        }
    }

    [Fact]
    public async Task Oversized_tree_caps_processes_and_encoded_bytes()
    {
        var records = Enumerable.Range(1, 200).Select(Record).ToArray();
        using var scope = AcceptanceLaneStallTreeSnapshot.PushInputsForTests(new(
            Walk: _ => records,
            Read: _ => new(1, 64, Enumerable.Range(1, 64)
                .Select(id => new AcceptanceLaneStallTreeSnapshot.ThreadEntry(id, "Wait", "Executive")).ToArray()),
            Delay: (_, _) => Task.CompletedTask));

        var snapshot = await AcceptanceLaneStallTreeSnapshot.CaptureAsync(1);
        Assert.Equal(AcceptanceLaneStallTreeSnapshot.ProcessCap, snapshot.Processes.Count);
        Assert.Equal(200 - AcceptanceLaneStallTreeSnapshot.ProcessCap, snapshot.OmittedProcesses);
        var line = snapshot.ToLine("lane\n\"unicode 雪");
        Assert.True(Encoding.UTF8.GetByteCount(line) <= AcceptanceLaneStallTreeSnapshot.SizeCapBytes);
        Assert.DoesNotContain('\n', line);
        Assert.DoesNotContain('\r', line);
        using var json = Parse(line);
        var encoded = json.RootElement.GetProperty("Snapshot");
        Assert.Contains("process-cap", encoded.GetProperty("Truncated").GetString());
        Assert.Contains("size-cap", encoded.GetProperty("Truncated").GetString());
        Assert.True(encoded.GetProperty("OmittedProcesses").GetInt32() > snapshot.OmittedProcesses);
        Assert.InRange(encoded.GetProperty("Processes").GetArrayLength(), 1, snapshot.Processes.Count);
    }

    [Fact]
    public async Task Small_tree_retains_readings_threads_and_has_no_truncation()
    {
        var reads = 0;
        using var scope = AcceptanceLaneStallTreeSnapshot.PushInputsForTests(new(
            Walk: _ => [Record(1)],
            Read: _ => new(Interlocked.Increment(ref reads), 1, [new(7, "Wait", "Executive")]),
            Delay: (interval, _) =>
            {
                Assert.Equal(TimeSpan.FromSeconds(5), interval);
                return Task.CompletedTask;
            }));

        var snapshot = await AcceptanceLaneStallTreeSnapshot.CaptureAsync(1);
        var entry = Assert.Single(snapshot.Processes);
        Assert.Equal(1d, entry.CpuSecondsFirst);
        Assert.Equal(2d, entry.CpuSecondsSecond);
        Assert.Equal("Executive", Assert.Single(entry.Threads).WaitReason);
        Assert.Null(snapshot.Truncated);
        Assert.Equal(0, snapshot.OmittedProcesses);
        using var json = Parse(snapshot.ToLine("lane"));
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("Snapshot").GetProperty("Truncated").ValueKind);
    }

    [Fact]
    public async Task Access_denied_keeps_an_unreadable_identity()
    {
        using var scope = AcceptanceLaneStallTreeSnapshot.PushInputsForTests(new(
            Walk: _ => [Record(1)], Read: _ => throw new UnauthorizedAccessException("denied"),
            Delay: (_, _) => Task.CompletedTask));
        var snapshot = await AcceptanceLaneStallTreeSnapshot.CaptureAsync(1);
        var entry = Assert.Single(snapshot.Processes);
        Assert.Equal(1, entry.Pid);
        Assert.Equal(Record(1).StartedAt, entry.StartedAt);
        Assert.Contains("UnauthorizedAccessException", entry.Unreadable);
        Assert.Null(entry.CpuSecondsFirst);
        Assert.Null(entry.CpuSecondsSecond);
    }

    [Fact]
    public async Task Blocked_reader_returns_deadline_diagnostic()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var departed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        using var scope = AcceptanceLaneStallTreeSnapshot.PushInputsForTests(new(
            Walk: _ => [Record(1)], Read: _ =>
            {
                entered.TrySetResult();
                try { release.Wait(); }
                finally { departed.TrySetResult(); }
                return new(1, 0, []);
            }, Deadline: TimeSpan.FromSeconds(1)));
        try
        {
            var capture = AcceptanceLaneStallTreeSnapshot.CaptureAsync(1);
            await entered.Task.WaitAsync(TimeSpan.FromMinutes(2)); // Hang detector: reader entered.
            var snapshot = await capture.WaitAsync(TimeSpan.FromMinutes(2)); // Hang detector: deadline published.
            Assert.Equal("deadline", snapshot.Unavailable);
            using var json = Parse(snapshot.ToLine("lane"));
            Assert.Equal("deadline", json.RootElement.GetProperty("Snapshot").GetProperty("Unavailable").GetString());
        }
        finally
        {
            release.Set();
            await departed.Task.WaitAsync(TimeSpan.FromMinutes(2)); // Hang detector: blocked reader released.
        }
    }

    [Fact]
    public async Task Cancelled_interval_keeps_first_reading()
    {
        using var stop = new CancellationTokenSource();
        using var scope = AcceptanceLaneStallTreeSnapshot.PushInputsForTests(new(
            Walk: _ => [Record(1)], Read: _ => new(3, 0, []),
            Delay: (_, token) => { stop.Cancel(); return Task.FromCanceled(token); }));
        var snapshot = await AcceptanceLaneStallTreeSnapshot.CaptureAsync(1, stop.Token);
        Assert.Equal(3d, Assert.Single(snapshot.Processes).CpuSecondsFirst);
        Assert.Null(Assert.Single(snapshot.Processes).CpuSecondsSecond);
        Assert.Equal("cancelled-interval", snapshot.Unavailable);
    }

    private static AcceptanceLaneStallMonitor CreateMonitor() =>
        AcceptanceLaneStallMonitor.ForHeartbeat(
            new GateHeartbeatContext(null, "verification-check", "fake lane", null, "unused", null, "fake", null),
            new FixedTimeProvider())!;

    private static GateHeartbeatSnapshot Heartbeat() =>
        new(null, "verification-check", "fake lane", null, null, null, "running",
            DateTimeOffset.MinValue, DateTimeOffset.MaxValue, DateTimeOffset.MinValue, 0, 0, 0);

    private sealed class FixedTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch;
    }

    private static ProcessInspectionRecord Record(int pid) => new(pid, pid == 1 ? 0 : 1,
        new string('雪', 256), null, DateTimeOffset.UnixEpoch, new string('雪', 256), ProcessInspectionStatus.Available);

    private static JsonDocument Parse(string line)
    {
        Assert.StartsWith("LANE_STALL_TREE ", line);
        return JsonDocument.Parse(line["LANE_STALL_TREE ".Length..]);
    }
}
