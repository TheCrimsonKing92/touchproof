using System.Text;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;

// Parallel-safe: each test owns its health file and cursor.
public sealed class ExecutorSlotSampleReaderCacheTests
{
    [Fact]
    public void RepeatedReads_ProcessOnlyAppendsAndEvictExpiredRows()
    {
        using var fixture = new ExecutorSlotControllerFixture();
        var path = Path.Combine(fixture.Root, "health.jsonl");
        var reader = new ExecutorSlotSampleReader();
        var at = fixture.Now;
        File.WriteAllText(path, Row("old", at.AddDays(-2)) + Row("recent", at));
        Assert.Equal("recent", Assert.Single(reader.ReadRecent(path, "exec", at.AddHours(-24), at)).AttemptId);
        Assert.Equal(new FileInfo(path).Length, reader.LastReadBytes);
        Assert.Equal(1, reader.RetainedRows);
        Assert.Single(reader.ReadRecent(path, "exec", at.AddHours(-24), at));
        Assert.Equal(0, reader.LastReadBytes);
        var appended = Row("new", at.AddMinutes(1));
        File.AppendAllText(path, appended);
        Assert.Equal(2, reader.ReadRecent(path, "exec", at.AddHours(-24), at.AddMinutes(1)).Count);
        Assert.Equal(Encoding.UTF8.GetByteCount(appended), reader.LastReadBytes);
        Assert.Empty(reader.ReadRecent(path, "exec", at.AddDays(1), at.AddDays(2)));
        Assert.Equal(0, reader.RetainedRows);
        Assert.Equal(0, reader.LastReadBytes);
    }

    [Fact]
    public void PartialAppend_IsRetriedOnceCompletedWithoutDuplicatingRows()
    {
        using var fixture = new ExecutorSlotControllerFixture();
        var path = Path.Combine(fixture.Root, "health.jsonl");
        var reader = new ExecutorSlotSampleReader();
        var line = Row("partial", fixture.Now);
        File.WriteAllText(path, line[..^3]);
        Assert.Empty(reader.ReadRecent(path, "exec", fixture.Now.AddHours(-1), fixture.Now));
        File.AppendAllText(path, line[^3..]);
        Assert.Equal("partial", Assert.Single(reader.ReadRecent(path, "exec", fixture.Now.AddHours(-1), fixture.Now)).AttemptId);
        Assert.Single(reader.ReadRecent(path, "exec", fixture.Now.AddHours(-1), fixture.Now));
        Assert.Equal(0, reader.LastReadBytes);
    }

    [Fact]
    public void ReplacementOrBroaderLookback_RebuildsTheCursor()
    {
        using var fixture = new ExecutorSlotControllerFixture();
        var path = Path.Combine(fixture.Root, "health.jsonl");
        var reader = new ExecutorSlotSampleReader();
        File.WriteAllText(path, Row("old", fixture.Now.AddDays(-2)) + Row("recent", fixture.Now));
        Assert.Single(reader.ReadRecent(path, "exec", fixture.Now.AddDays(-1), fixture.Now));
        Assert.Equal(2, reader.ReadRecent(path, "exec", fixture.Now.AddDays(-3), fixture.Now).Count);
        File.WriteAllText(path, Row("replacement", fixture.Now));
        Assert.Equal("replacement", Assert.Single(reader.ReadRecent(path, "exec", fixture.Now.AddDays(-1), fixture.Now)).AttemptId);
        Assert.Equal(1, reader.RetainedRows);
    }

    [Fact]
    public void LookbackBoundary_PreservesEarlierOverlappingAttempts()
    {
        using var fixture = new ExecutorSlotControllerFixture();
        var path = Path.Combine(fixture.Root, "health.jsonl");
        var at = fixture.Now;
        File.WriteAllText(path, Row("prior", at.AddSeconds(-1), at.AddSeconds(-10)) +
            Row("selected", at.AddSeconds(5), at.AddSeconds(-5)));
        var reader = new ExecutorSlotSampleReader();
        var sample = Assert.Single(reader.ReadRecent(path, "exec", at, at.AddSeconds(5)));
        Assert.Equal("selected", sample.AttemptId);
        Assert.Equal(2, sample.Concurrency);
        Assert.Equal(2, reader.RetainedRows);
    }

    [Fact]
    public void LateTerminalRow_ReloadsEvictedIntervalsBeforeAttributingConcurrency()
    {
        using var fixture = new ExecutorSlotControllerFixture();
        var path = Path.Combine(fixture.Root, "health.jsonl");
        var at = fixture.Now;
        File.WriteAllText(path, Row("prior1", at.AddSeconds(-10), at.AddSeconds(-30)) +
            Row("prior2", at.AddSeconds(-10), at.AddSeconds(-30)) +
            Row("selected", at.AddSeconds(5), at.AddSeconds(-5)));
        var reader = new ExecutorSlotSampleReader();
        Assert.Equal(1, Assert.Single(reader.ReadRecent(path, "exec", at, at.AddSeconds(5))).Concurrency);
        Assert.Equal(1, reader.RetainedRows);
        File.AppendAllText(path, Row("late", at.AddSeconds(10), at.AddSeconds(-20)));
        var rows = reader.ReadRecent(path, "exec", at, at.AddSeconds(10));
        Assert.Equal(3, rows.Single(row => row.AttemptId == "late").Concurrency);
        Assert.Equal(4, reader.RetainedRows);
    }

    private static string Row(string id, DateTimeOffset at, DateTimeOffset? start = null) => JsonSerializer.Serialize(new
    {
        observed_at = at, executor_id = "exec", gate_attempt_id = id, lane = "lane", outcome = "accepted",
        attempt = new { last_status = new { seconds = 10 }, steps = new[] { new { started_at = start ?? at.AddSeconds(-10) } } }
    }) + "\n";
}
