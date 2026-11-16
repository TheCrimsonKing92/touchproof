using System.Text;
using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: the process tree, readings and interval are scoped synthetic inputs.
public sealed class AcceptanceLaneStallTreeSnapshotTests
{
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

    private static ProcessInspectionRecord Record(int pid) => new(pid, pid == 1 ? 0 : 1,
        new string('雪', 256), null, DateTimeOffset.UnixEpoch, new string('雪', 256), ProcessInspectionStatus.Available);

    private static JsonDocument Parse(string line)
    {
        Assert.StartsWith("LANE_STALL_TREE ", line);
        return JsonDocument.Parse(line["LANE_STALL_TREE ".Length..]);
    }
}
