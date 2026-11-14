using System.Text;
using System.Text.Json;
using Mcg.AgentOrchestrator.Core;

// Parallel-safe: serialization is local and telemetry writes into a private directory.
public sealed class GoalTaskHistorySerializedSizeTests(ITestOutputHelper output)
{
    [Fact]
    public void TenEntriesPerTaskReportPotentialDocumentSavings()
    {
        var goal = GoalTaskHistoryTestFixture.Snapshot();
        Assert.All(goal.Tasks, task =>
        {
            Assert.Equal(10, task.VerificationHistory!.Count);
            Assert.Equal(10, task.DispatchHistory!.Count);
        });
        var emptied = goal with { Tasks = goal.Tasks.Select(task => task with
            { VerificationHistory = [], DispatchHistory = [] }).ToArray() };
        var inlineBytes = Bytes(goal);
        var emptiedBytes = Bytes(emptied);
        var payloadBytes = goal.Tasks.Sum(task => Bytes(task.VerificationHistory) - 2 + Bytes(task.DispatchHistory) - 2);
        output.WriteLine($"inlineBytes={inlineBytes}; emptiedBytes={emptiedBytes}; difference={inlineBytes - emptiedBytes}");
        Assert.True(payloadBytes > 0);
        Assert.True(inlineBytes - emptiedBytes >= payloadBytes);
    }

    [Fact]
    public async Task SlowWriteRecordContainsExactGoalDocumentUtf8Bytes()
    {
        using var fixture = new GoalTaskHistoryTestFixture();
        var goal = GoalTaskHistoryTestFixture.Snapshot();
        await fixture.Repository.SaveGoalSnapshotsAsync([goal]);
        var lines = File.ReadAllLines(fixture.DiagnosticsPath);
        using var receipt = JsonDocument.Parse(lines.Last(line => line.Contains("sqlite-state-write-hold", StringComparison.Ordinal)));
        Assert.Equal(Encoding.UTF8.GetByteCount(fixture.RawGoal(goal.Id)), receipt.RootElement.GetProperty("serializedBytes").GetInt64());
        Assert.Equal(1, receipt.RootElement.GetProperty("rowsWritten").GetInt64());
        Assert.Equal("commit", receipt.RootElement.GetProperty("disposition").GetString());
    }

    private static int Bytes<T>(T value) => Encoding.UTF8.GetByteCount(GoalTaskHistoryTestFixture.Json(value));
}
