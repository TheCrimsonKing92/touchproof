// Parallel-safe: each case owns its database.
public sealed class GoalTimelineInTableInlineTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task AbsentOrNullMarker_UsesInlineDespiteStrayRowsOrMissingTable(bool explicitNull, bool missingTable)
    {
        using var support = new GoalTimelineTestSupport();
        var goal = GoalTimelineTestSupport.Snapshot();
        await support.SeedAsync(goal, marked: false);
        if (explicitNull)
            support.Fixture.Execute("UPDATE goals SET snapshot_json = json_set(snapshot_json, '$.TimelineInTable', NULL)");
        if (missingTable) support.Fixture.Execute("DROP TABLE goal_timeline");
        else support.Fixture.Execute("UPDATE goal_timeline SET entry_json = 'stray invalid rows must be ignored'");
        foreach (var path in GoalTimelineTestSupport.Paths())
        {
            var loaded = await support.ReadAsync(path, goal);
            Assert.Equal(GoalTimelineTestSupport.Json(goal.Timeline), GoalTimelineTestSupport.Json(loaded.Timeline));
        }
    }
}
