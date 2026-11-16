using System.Text.Json.Nodes;

// Parallel-safe: each case owns its database and connections.
public sealed class GoalTimelineInTableReadTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Marked_AllHydratingPaths_PreserveSequenceAndEveryPayload(bool archived)
    {
        using var support = new GoalTimelineTestSupport();
        var goal = GoalTimelineTestSupport.Snapshot();
        await support.SeedAsync(goal);
        Assert.Null(JsonNode.Parse(support.Fixture.RawGoal(goal.Id))!["Timeline"]);
        if (archived)
            Assert.Equal(1, await support.Repository.ArchiveTerminalGoalSnapshotsAsync(GoalTimelineTestSupport.At.AddDays(1)));
        var before = support.Fixture.RawGoal(goal.Id);
        foreach (var path in GoalTimelineTestSupport.Paths(archived))
        {
            var actual = await support.ReadAsync(path, goal);
            Assert.Equal(GoalTimelineTestSupport.Json(goal.Timeline), GoalTimelineTestSupport.Json(actual.Timeline));
        }
        Assert.Equal(before, support.Fixture.RawGoal(goal.Id));
    }

    [Fact]
    public async Task ZeroCount_RequiresNoRowsAndIgnoresInlineTimeline()
    {
        using var support = new GoalTimelineTestSupport();
        var goal = GoalTimelineTestSupport.Snapshot();
        await support.SeedAsync(goal);
        support.Fixture.Execute("DELETE FROM goal_timeline; UPDATE goals SET snapshot_json = json_set(snapshot_json, '$.TimelineInTable', 0, '$.Timeline', json('[{}]'))");
        Assert.Empty((await support.Repository.LoadGoalAsync(new(goal.Id)))!.Timeline);
    }
}
