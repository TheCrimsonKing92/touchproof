using System.Text.Json.Nodes;
using Mcg.AgentOrchestrator.Core;

// Parallel-safe: each save path owns its database.
public sealed class GoalTimelineInTableSaveTests
{
    [Theory]
    [InlineData("snapshots")]
    [InlineData("kernel")]
    [InlineData("cas")]
    [InlineData("state-cas")]
    [InlineData("merge")]
    public async Task SaveAfterMarkedLoad_WritesInlineWithoutMarkerAndRetainsRows(string path)
    {
        using var support = new GoalTimelineTestSupport();
        var goal = GoalTimelineTestSupport.Snapshot();
        await support.SeedAsync(goal);
        var rows = support.Rows();
        var id = new GoalId(goal.Id);
        switch (path)
        {
            case "snapshots": await support.Repository.SaveGoalSnapshotsAsync([(await support.Repository.LoadGoalAsync(id))!]); break;
            case "kernel": await support.Repository.SaveAsync(await support.Repository.LoadGoalsAsync([id])); break;
            case "cas": await support.Repository.TransactGoalAsync(id, (loaded, _) => Task.FromResult((true, loaded, true))); break;
            case "state-cas": await support.Repository.TransactGoalStateAsync(id, (loaded, _) => Task.FromResult((true, loaded, true))); break;
            default:
                var loaded = (await support.Repository.LoadGoalAsync(id))!;
                await support.Repository.SaveGoalSnapshotsWithMergeAsync([new(loaded, loaded)]);
                break;
        }
        var document = JsonNode.Parse(support.Fixture.RawGoal(goal.Id))!.AsObject();
        Assert.False(document.ContainsKey("TimelineInTable"));
        // Compare every entry and payload without depending on JSON character escaping.
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(GoalTimelineTestSupport.Json(goal.Timeline)), document["Timeline"]),
            "Saved inline Timeline differs from the loaded entries.");
        Assert.Equal(rows, support.Rows());
        Assert.Equal(GoalTimelineTestSupport.Json(goal), GoalTimelineTestSupport.Json(await support.Repository.LoadGoalAsync(id)));
    }
}
