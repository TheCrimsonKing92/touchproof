using System.Text.Json.Nodes;
using Mcg.AgentOrchestrator.Core;

// Parallel-safe: triggers and audit tables belong to each case's private database.
public sealed class GoalTimelineInTableWriteTests
{
    [Fact]
    public async Task AppendAfterReload_WritesOnlyNewSequences()
    {
        using var support = new GoalTimelineTestSupport();
        var goal = GoalTimelineTestSupport.Snapshot();
        await support.Repository.SaveGoalSnapshotsAsync([goal]);
        await support.AssertMarkedAsync(goal);
        var prefix = support.Fixture.Rows("SELECT rowid || '|' || entry_json FROM goal_timeline ORDER BY sequence");
        Audit(support);
        var loaded = (await support.Repository.LoadGoalAsync(new(goal.Id)))!;
        var appended = loaded with { Timeline = [.. loaded.Timeline,
            loaded.Timeline[^1] with { Message = "appended one" }, loaded.Timeline[^1] with { Message = "appended two" }] };
        await support.Repository.SaveGoalSnapshotsAsync([appended]);
        Assert.Equal(new[] { "insert|3", "insert|4" }, support.Fixture.Rows("SELECT operation || '|' || sequence FROM timeline_audit ORDER BY rowid"));
        Assert.Equal(prefix, support.Fixture.Rows("SELECT rowid || '|' || entry_json FROM goal_timeline WHERE sequence < 3 ORDER BY sequence"));
        await support.AssertMarkedAsync(appended);
    }

    [Theory]
    [InlineData("changed")]
    [InlineData("shorter")]
    [InlineData("gap")]
    public async Task Mismatch_ReplacesStoredRows(string defect)
    {
        using var support = new GoalTimelineTestSupport();
        var goal = GoalTimelineTestSupport.Snapshot();
        await support.Repository.SaveGoalSnapshotsAsync([goal]);
        await support.AssertMarkedAsync(goal);
        if (defect == "gap") support.Fixture.Execute("UPDATE goal_timeline SET sequence = 4 WHERE sequence = 2");
        var replacement = defect switch
        {
            "changed" => goal with { Timeline = [goal.Timeline[0], goal.Timeline[1] with { Message = "changed earlier" }, goal.Timeline[2]] },
            "shorter" => goal with { Timeline = [goal.Timeline[0]] },
            _ => goal
        };
        Audit(support);
        await support.Repository.SaveGoalSnapshotsAsync([replacement]);
        Assert.Equal(3, support.Fixture.Rows("SELECT operation FROM timeline_audit WHERE operation = 'delete'").Length);
        Assert.Equal(replacement.Timeline.Count, support.Fixture.Rows("SELECT operation FROM timeline_audit WHERE operation = 'insert'").Length);
        await support.AssertMarkedAsync(replacement);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnprovenRows_SaveInlineAndRemoveLeftovers(bool missingTable)
    {
        using var support = new GoalTimelineTestSupport();
        var goal = GoalTimelineTestSupport.Snapshot();
        await support.Repository.SaveGoalSnapshotsAsync([goal]);
        await support.AssertMarkedAsync(goal);
        if (missingTable) support.Fixture.Execute("DROP TABLE goal_timeline");
        else support.Fixture.Execute("""
            UPDATE goal_timeline SET entry_json = '{}' WHERE sequence = 0;
            CREATE TRIGGER corrupt_timeline AFTER INSERT ON goal_timeline WHEN NEW.sequence = 1
            BEGIN UPDATE goal_timeline SET entry_json = '{}' WHERE goal_id = NEW.goal_id AND sequence = NEW.sequence; END;
            """);
        await support.Repository.SaveGoalSnapshotsAsync([goal]);
        var document = JsonNode.Parse(support.Fixture.RawGoal(goal.Id))!.AsObject();
        Assert.False(document.ContainsKey("TimelineInTable"));
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(GoalTimelineTestSupport.Json(goal.Timeline)), document["Timeline"]));
        if (!missingTable) Assert.Empty(support.Rows());
        Assert.Equal(GoalTimelineTestSupport.Json(goal), GoalTimelineTestSupport.Json(await support.Repository.LoadGoalAsync(new(goal.Id))));
    }

    [Fact]
    public async Task EmptyTimeline_UsesZeroMarker()
    {
        using var support = new GoalTimelineTestSupport();
        var goal = GoalTimelineTestSupport.Snapshot() with { Timeline = [] };
        await support.Repository.SaveGoalSnapshotsAsync([goal]);
        await support.AssertMarkedAsync(goal);
    }

    private static void Audit(GoalTimelineTestSupport support) => support.Fixture.Execute("""
        CREATE TABLE timeline_audit(operation TEXT, sequence INTEGER);
        CREATE TRIGGER timeline_insert AFTER INSERT ON goal_timeline
        BEGIN INSERT INTO timeline_audit VALUES ('insert', NEW.sequence); END;
        CREATE TRIGGER timeline_update AFTER UPDATE ON goal_timeline
        BEGIN INSERT INTO timeline_audit VALUES ('update', NEW.sequence); END;
        CREATE TRIGGER timeline_delete AFTER DELETE ON goal_timeline
        BEGIN INSERT INTO timeline_audit VALUES ('delete', OLD.sequence); END;
        """);
}
