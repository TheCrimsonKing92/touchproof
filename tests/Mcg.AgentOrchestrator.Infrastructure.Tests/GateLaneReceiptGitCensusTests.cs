using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: sidecars, TRX files and receipts belong to each case's temporary root.
public sealed class GateLaneReceiptGitCensusTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("receipt-git-").FullName;

    [Theory]
    [InlineData(true, 3, 75.5, false)]
    [InlineData(true, 0, 0, false)]
    [InlineData(false, 0, 0, false)]
    [InlineData(true, 3, 75.5, true)]
    [InlineData(true, 0, 0, true)]
    public void Record_OptionalCensus_WritesMeasuredFieldsOnly(bool measured, int launches, double wall, bool reused)
    {
        var trx = Path.Combine(root, "lane.trx");
        File.WriteAllText(trx, """
            <TestRun><Results><UnitTestResult testName="Example.Tests.Method" outcome="Passed" duration="00:00:05" /></Results></TestRun>
            """);
        var row = new Dictionary<string, object>
        {
            ["test_name"] = "Example.Tests.Method", ["wait_ms"] = 0, ["held_ms"] = 5,
            ["slot_count"] = 2, ["process_id"] = Environment.ProcessId
        };
        if (measured)
        {
            row["git_launches"] = launches;
            if (launches > 0) row["git_wall_ms"] = wall;
            row["git_callers"] = new Dictionary<string, long>
            {
                [launches > 0 ? "src:Example.Read<-Parent.Method|status" : "other"] = launches
            };
        }
        if (launches == 0) Assert.False(row.ContainsKey("git_wall_ms"));
        File.WriteAllText(LaneSlotTimingReader.PathFor(trx), JsonSerializer.Serialize(row));
        var result = new AcceptanceCheckResult("Alpha", true, 0, null,
            TestResultPaths: [trx], TestResultAttemptId: "attempt-a", TestResultIsExplicitCrossAttemptReuse: reused);
        GateLaneReceiptLedger.Record(root, "goal", "Alpha", result, TimeSpan.FromSeconds(10), DateTimeOffset.UnixEpoch);
        using var written = JsonDocument.Parse(Assert.Single(File.ReadAllLines(GateLaneReceiptLedger.ResolveStorePath(root))));
        var receipt = written.RootElement;
        Assert.Equal(10000, receipt.GetProperty("wall_ms").GetDouble());
        if (measured && !reused)
        {
            Assert.Equal("attempt-a", receipt.GetProperty("attempt_id").GetString());
            Assert.Equal(launches, receipt.GetProperty("slot_git_launches").GetInt64());
            Assert.Equal(wall, receipt.GetProperty("slot_git_wall_ms").GetDouble());
            var site = Assert.Single(receipt.GetProperty("slot_git_call_sites").EnumerateObject());
            Assert.Equal(launches > 0 ? "Example.Read" : "other", site.Name);
            Assert.Equal(launches, site.Value.GetInt64());
        }
        else
            foreach (var key in new[] { "attempt_id", "slot_git_launches", "slot_git_wall_ms", "slot_git_call_sites" })
                Assert.False(receipt.TryGetProperty(key, out _));
    }

    public void Dispose() => Directory.Delete(root, true);
}
