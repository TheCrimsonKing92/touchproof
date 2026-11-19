using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

public sealed class GoalTaskBuildHistoryDocumentSizeTests(ITestOutputHelper output)
{
    [Fact]
    public async Task FourTasksWithTenInvocationsProduceSmallerDocument()
    {
        using var fixture = new GoalTaskHistoryTestFixture();
        var goal = GoalTaskBuildHistoryTestSupport.Snapshot();
        await fixture.Repository.SaveGoalSnapshotsAsync([goal]);
        var stored = fixture.RawGoal(goal.Id);
        // Reconstruct the old build shape while retaining the other history projections.
        var legacyDocument = JsonNode.Parse(stored)!;
        var legacyTasks = legacyDocument["Tasks"]!.AsArray();
        Assert.Equal(4, legacyTasks.Count);
        for (var index = 0; index < legacyTasks.Count; index++)
        {
            var task = legacyTasks[index]!.AsObject();
            Assert.True(task.Remove("WorkerBuildInvocationsInTable"));
            task["WorkerBuildInvocations"] = JsonSerializer.SerializeToNode(
                goal.Tasks[index].WorkerBuildInvocations, GoalTaskHistoryTestFixture.Options);
        }
        var legacy = legacyDocument.ToJsonString(GoalTaskHistoryTestFixture.Options);
        var legacyBytes = Encoding.UTF8.GetByteCount(legacy);
        var storedBytes = Encoding.UTF8.GetByteCount(stored);
        output.WriteLine($"Legacy inline document {legacyBytes} bytes. Row-backed document {storedBytes} bytes.");
        Assert.True(storedBytes < legacyBytes);

        using var storedDocument = JsonDocument.Parse(stored);
        var storedTasks = storedDocument.RootElement.GetProperty("Tasks").EnumerateArray().ToArray();
        Assert.Equal(legacyTasks.Count, storedTasks.Length);
        Assert.All(legacyTasks, task => Assert.Equal(10, task!["WorkerBuildInvocations"]!.AsArray().Count));
        Assert.All(storedTasks, task =>
        {
            var inline = Assert.Single(task.GetProperty("WorkerBuildInvocations").EnumerateArray());
            Assert.Equal(JsonValueKind.Null, inline.GetProperty("Outcome").ValueKind);
        });
    }
}
