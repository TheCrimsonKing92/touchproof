using Mcg.AgentOrchestrator.Core;

// Parallel-safe: each save path owns its database.
public sealed class GoalTaskHistoryLatestInTableSaveTests
{
    [Theory]
    [InlineData("snapshots")]
    [InlineData("kernel")]
    [InlineData("cas")]
    [InlineData("state-cas")]
    [InlineData("merge")]
    public async Task LoadFlaggedThenSave_PreservesDocumentRowsAndFlags(string path)
    {
        using var fixture = new GoalTaskHistoryTestFixture();
        var goal = GoalTaskHistoryTestFixture.LatestSnapshot();
        await fixture.Repository.SaveGoalSnapshotsAsync([goal]);
        fixture.AssertLatestRepresentation(goal);
        var document = fixture.RawGoal(goal.Id);
        var version = fixture.Rows("SELECT version || '|' || updated_at FROM goals");
        var rows = fixture.HistoryRows();
        var id = new GoalId(goal.Id);
        if (path == "snapshots")
            await fixture.Repository.SaveGoalSnapshotsAsync([(await fixture.Repository.LoadGoalAsync(id))!]);
        else if (path == "kernel")
            await fixture.Repository.SaveAsync(await fixture.Repository.LoadGoalsAsync([id]));
        else if (path == "cas")
            await fixture.Repository.TransactGoalAsync(id, (loaded, _) => Task.FromResult((true, loaded, true)));
        else if (path == "state-cas")
            await fixture.Repository.TransactGoalStateAsync(id, (loaded, _) => Task.FromResult((true, loaded, true)));
        else
        {
            var loaded = (await fixture.Repository.LoadGoalAsync(id))!;
            await fixture.Repository.SaveGoalSnapshotsWithMergeAsync([new(loaded, loaded)]);
        }
        fixture.AssertLatestRepresentation(goal);
        Assert.Equal(document, fixture.RawGoal(goal.Id));
        Assert.Equal(rows, fixture.HistoryRows());
        Assert.Equal(GoalTaskHistoryTestFixture.Json(goal),
            GoalTaskHistoryTestFixture.Json(await fixture.Repository.LoadGoalAsync(id)));
        if (path == "snapshots")
        {
            Assert.Equal(version, fixture.Rows("SELECT version || '|' || updated_at FROM goals"));
            Assert.Equal(0, fixture.ReportedBytes());
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ReplaceAppendThenClear_RecomputesFlagsAndKeepsRows(bool verification)
    {
        using var fixture = new GoalTaskHistoryTestFixture();
        var goal = GoalTaskHistoryTestFixture.Snapshot(taskCount: 1);
        await fixture.Repository.SaveGoalSnapshotsAsync([goal]);
        fixture.AssertLatestRepresentation(goal);
        var count = fixture.HistoryRows().Length;
        var task = goal.Tasks[0];
        var nextVerification = task.LastVerification! with { Command = "replacement verification" };
        var nextDispatch = task.LastDispatch! with { Command = "replacement dispatch" };
        task = verification ? task with
        {
            LastVerification = nextVerification, VerificationHistory = [.. task.VerificationHistory!, nextVerification]
        } : task with
        {
            LastDispatch = nextDispatch, DispatchHistory = [.. task.DispatchHistory!, nextDispatch]
        };
        goal = goal with { Tasks = [task] };
        await fixture.Repository.SaveGoalSnapshotsAsync([goal]);
        fixture.AssertLatestRepresentation(goal);
        Assert.Equal(count + 1, fixture.HistoryRows().Length);
        var kind = verification ? "verification" : "dispatch";
        var expected = verification ? GoalTaskHistoryTestFixture.Json(nextVerification) : GoalTaskHistoryTestFixture.Json(nextDispatch);
        Assert.Equal(expected, Assert.Single(fixture.Rows(
            $"SELECT entry_json FROM goal_task_history WHERE kind = '{kind}' ORDER BY sequence DESC LIMIT 1")));
        Assert.Equal(GoalTaskHistoryTestFixture.Json(goal),
            GoalTaskHistoryTestFixture.Json(await fixture.Repository.LoadGoalAsync(new(goal.Id))));
        var rows = fixture.HistoryRows();
        goal = goal with { Tasks = [verification ? task with { LastVerification = null } : task with { LastDispatch = null }] };
        await fixture.Repository.SaveGoalSnapshotsAsync([goal]);
        fixture.AssertLatestRepresentation(goal, !verification, verification);
        Assert.Equal(rows, fixture.HistoryRows());
        Assert.Equal(GoalTaskHistoryTestFixture.Json(goal),
            GoalTaskHistoryTestFixture.Json(await fixture.Repository.LoadGoalAsync(new(goal.Id))));
    }

    [Fact]
    public async Task SameCountReplacement_AdvancesVersionEvenWhenDocumentIsIdentical()
    {
        using var fixture = new GoalTaskHistoryTestFixture();
        var goal = GoalTaskHistoryTestFixture.Snapshot(taskCount: 1);
        await fixture.Repository.SaveGoalSnapshotsAsync([goal]);
        fixture.AssertLatestRepresentation(goal);
        var document = fixture.RawGoal(goal.Id);
        var version = int.Parse(Assert.Single(fixture.Rows("SELECT CAST(version AS TEXT) FROM goals")));
        var task = goal.Tasks[0];
        var entries = task.VerificationHistory!.ToArray();
        entries[^1] = entries[^1] with { StandardOutput = "revised newest entry" };
        goal = goal with { Tasks = [task with { VerificationHistory = entries, LastVerification = entries[^1] }] };
        await fixture.Repository.SaveGoalSnapshotsAsync([goal]);
        fixture.AssertLatestRepresentation(goal);
        Assert.Equal(document, fixture.RawGoal(goal.Id));
        Assert.Equal(version + 1, int.Parse(Assert.Single(fixture.Rows("SELECT CAST(version AS TEXT) FROM goals"))));
        Assert.Equal(GoalTaskHistoryTestFixture.Json(goal),
            GoalTaskHistoryTestFixture.Json(await fixture.Repository.LoadGoalAsync(new(goal.Id))));
    }
}
