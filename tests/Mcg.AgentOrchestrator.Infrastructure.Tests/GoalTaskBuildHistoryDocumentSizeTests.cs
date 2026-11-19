using System.Text;
using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class GoalTaskBuildHistoryDocumentSizeTests(ITestOutputHelper output)
{
    [Fact]
    public async Task FourTasksWithTenInvocationsProduceSmallerDocument()
    {
        using var fixture = new GoalTaskHistoryTestFixture();
        var goal = GoalTaskBuildHistoryTestSupport.Snapshot();
        // Match the repository's task history serializer and timeline projection.
        var legacy = new GoalTaskHistoryStore(GoalTaskHistoryTestFixture.Options)
            .PrepareWrite(goal, goal.Timeline.Count).Document;
        await fixture.Repository.SaveGoalSnapshotsAsync([goal]);
        var stored = fixture.RawGoal(goal.Id);
        var legacyBytes = Encoding.UTF8.GetByteCount(legacy);
        var storedBytes = Encoding.UTF8.GetByteCount(stored);
        output.WriteLine($"Legacy inline document {legacyBytes} bytes. Row-backed document {storedBytes} bytes.");
        Assert.True(storedBytes < legacyBytes);

        using var legacyDocument = JsonDocument.Parse(legacy);
        using var storedDocument = JsonDocument.Parse(stored);
        var legacyTasks = legacyDocument.RootElement.GetProperty("Tasks").EnumerateArray().ToArray();
        var storedTasks = storedDocument.RootElement.GetProperty("Tasks").EnumerateArray().ToArray();
        Assert.Equal(4, legacyTasks.Length);
        Assert.Equal(legacyTasks.Length, storedTasks.Length);
        Assert.All(legacyTasks, task => Assert.Equal(10, task.GetProperty("WorkerBuildInvocations").GetArrayLength()));
        Assert.All(storedTasks, task =>
        {
            var inline = Assert.Single(task.GetProperty("WorkerBuildInvocations").EnumerateArray());
            Assert.Equal(JsonValueKind.Null, inline.GetProperty("Outcome").ValueKind);
        });
    }
}
