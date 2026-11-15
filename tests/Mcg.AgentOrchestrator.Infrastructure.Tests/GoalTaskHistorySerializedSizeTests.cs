using System.Text;
using System.Text.Json;
using Mcg.AgentOrchestrator.Core;

// Parallel-safe: serialization is local and telemetry writes into a private directory.
public sealed class GoalTaskHistorySerializedSizeTests(ITestOutputHelper output)
{
    [Fact]
    public async Task TenEntriesPerTaskReportActualSavedAndParsedDocumentSavings()
    {
        using var fixture = new GoalTaskHistoryTestFixture();
        var goal = GoalTaskHistoryTestFixture.Snapshot();
        Assert.All(goal.Tasks, task =>
        {
            Assert.Equal(10, task.VerificationHistory!.Count);
            Assert.Equal(10, task.DispatchHistory!.Count);
        });
        var inlineBytes = Bytes(goal);
        var payloadBytes = goal.Tasks.Sum(task => task.VerificationHistory!.Sum(Bytes) + task.DispatchHistory!.Sum(Bytes));
        await fixture.Repository.SaveGoalSnapshotsAsync([goal]);
        var markedText = fixture.RawGoal(goal.Id);
        using var marked = JsonDocument.Parse(markedText);
        using var inline = JsonDocument.Parse(GoalTaskHistoryTestFixture.Json(goal));
        var parsedInlineBytes = Encoding.UTF8.GetByteCount(inline.RootElement.GetRawText());
        var parsedMarkedBytes = Encoding.UTF8.GetByteCount(marked.RootElement.GetRawText());
        var savedBytes = ReportedBytes(fixture);
        output.WriteLine($"serialized: inlineBytes={inlineBytes}; markedBytes={savedBytes}; parsed: inlineBytes={parsedInlineBytes}; markedBytes={parsedMarkedBytes}; historyEntryBytes={payloadBytes}");
        Assert.Equal(inlineBytes, parsedInlineBytes);
        Assert.Equal(parsedMarkedBytes, savedBytes);
        Assert.True(payloadBytes > 0);
        Assert.True(savedBytes < inlineBytes);
        Assert.True(parsedInlineBytes - parsedMarkedBytes >= payloadBytes);
        Assert.Equal(GoalTaskHistoryTestFixture.Json(goal), GoalTaskHistoryTestFixture.Json(
            await fixture.Repository.LoadGoalAsync(new(goal.Id))));
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

    private static long ReportedBytes(GoalTaskHistoryTestFixture fixture)
    {
        using var receipt = JsonDocument.Parse(File.ReadAllLines(fixture.DiagnosticsPath)
            .Last(line => line.Contains("sqlite-state-write-hold", StringComparison.Ordinal)));
        return receipt.RootElement.GetProperty("serializedBytes").GetInt64();
    }
}
