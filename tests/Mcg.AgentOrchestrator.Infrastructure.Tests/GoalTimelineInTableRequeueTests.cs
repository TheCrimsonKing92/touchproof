using System.Text.Json.Nodes;
using Mcg.AgentOrchestrator.Core;

// Parallel-safe: utility invocations use private databases and do not redirect Console.
public sealed class GoalTimelineInTableRequeueTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Requeue_AppendsEventAtomicallyAndPreservesAuthority(bool marked)
    {
        using var support = new GoalTimelineTestSupport();
        var goal = GoalTimelineTestSupport.Snapshot();
        if (marked) await support.Repository.SaveGoalSnapshotsAsync([goal]);
        else await support.SeedAsync(goal, marked: false);
        var before = support.Rows();
        Assert.Equal(0, await Requeue(support, goal.Id));
        var document = JsonNode.Parse(support.Fixture.RawGoal(goal.Id))!.AsObject();
        if (marked)
        {
            Assert.False(document.ContainsKey("Timeline"));
            Assert.Equal(goal.Timeline.Count + 1, document["TimelineInTable"]!.GetValue<int>());
            Assert.Equal(before, support.Rows().Take(before.Length));
            Assert.Equal(before.Length + 1, support.Rows().Length);
        }
        else
        {
            Assert.False(document.ContainsKey("TimelineInTable"));
            Assert.Equal(goal.Timeline.Count + 1, document["Timeline"]!.AsArray().Count);
            Assert.Equal(before, support.Rows());
        }
        var loaded = (await support.Repository.LoadGoalAsync(new(goal.Id)))!;
        Assert.Equal(GoalStatus.Active, loaded.Status);
        Assert.Equal(GoalTimelineTestSupport.Json(goal.Timeline), GoalTimelineTestSupport.Json(loaded.Timeline.Take(goal.Timeline.Count)));
        Assert.Equal(ProgressKind.TaskRetried, loaded.Timeline[^1].Kind);
        Assert.Equal("retry fixture", loaded.Timeline[^1].Message);
        Assert.Equal(goal.Tasks[0].Id, loaded.Timeline[^1].TaskId);
        Assert.Null(loaded.Tasks[0].LastDispatch);
        Assert.Null(loaded.Tasks[0].LastVerification);
    }

    [Theory]
    [InlineData("count")]
    [InlineData("gap")]
    [InlineData("json")]
    [InlineData("nonobject")]
    [InlineData("marker")]
    [InlineData("missing-table")]
    public async Task InvalidMarkedStorage_IsRefusedWithoutAnyMutation(string defect)
    {
        using var support = new GoalTimelineTestSupport();
        var goal = GoalTimelineTestSupport.Snapshot();
        await support.Repository.SaveGoalSnapshotsAsync([goal]);
        await support.AssertMarkedAsync(goal);
        support.Fixture.Execute(defect switch
        {
            "count" => "UPDATE goals SET snapshot_json = json_set(snapshot_json, '$.TimelineInTable', 4)",
            "gap" => "UPDATE goal_timeline SET sequence = 4 WHERE sequence = 2",
            "json" => "UPDATE goal_timeline SET entry_json = 'bad json' WHERE sequence = 1",
            "nonobject" => "UPDATE goal_timeline SET entry_json = 'null' WHERE sequence = 1",
            "marker" => "UPDATE goals SET snapshot_json = json_set(snapshot_json, '$.TimelineInTable', -1)",
            _ => "DROP TABLE goal_timeline"
        });
        var before = support.Fixture.Rows("SELECT status || '|' || snapshot_json || '|' || updated_at || '|' || version FROM goals");
        var rows = defect == "missing-table" ? [] : support.Rows();
        Assert.NotEqual(0, await Requeue(support, goal.Id));
        Assert.Equal(before, support.Fixture.Rows("SELECT status || '|' || snapshot_json || '|' || updated_at || '|' || version FROM goals"));
        if (defect != "missing-table") Assert.Equal(rows, support.Rows());
    }

    [Fact]
    public async Task FailedDocumentUpdate_RollsBackTimelineAppend()
    {
        using var support = new GoalTimelineTestSupport();
        var goal = GoalTimelineTestSupport.Snapshot();
        await support.Repository.SaveGoalSnapshotsAsync([goal]);
        support.Fixture.Execute("CREATE TRIGGER reject_goal BEFORE UPDATE ON goals BEGIN SELECT RAISE(ABORT, 'fixture rejection'); END");
        var text = support.Fixture.RawGoal(goal.Id);
        var rows = support.Rows();
        Assert.NotEqual(0, await Requeue(support, goal.Id));
        Assert.Equal(text, support.Fixture.RawGoal(goal.Id));
        Assert.Equal(rows, support.Rows());
    }

    private static Task<int> Requeue(GoalTimelineTestSupport support, string id) =>
        OrchestratorSqliteTools.RunAsync(["requeue-task", "--db", support.Fixture.DatabasePath,
            "--task-number", "1", "--note", " retry fixture ", id]);
}
