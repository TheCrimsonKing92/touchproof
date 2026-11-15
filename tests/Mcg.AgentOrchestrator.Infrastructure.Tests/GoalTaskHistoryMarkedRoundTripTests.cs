using Mcg.AgentOrchestrator.Core;

// Parallel-safe: each case owns a database and kernel.
public sealed class GoalTaskHistoryMarkedRoundTripTests
{
    [Theory]
    [InlineData("ten")]
    [InlineData("empty")]
    [InlineData("null")]
    [InlineData("cap")]
    [InlineData("removed")]
    public async Task MarkedSaveLoadSavePreservesEachHistoryShape(string shape)
    {
        using var fixture = new GoalTaskHistoryTestFixture();
        var goal = GoalTaskHistoryTestFixture.Snapshot(entries: shape == "cap" ? 21 : shape is "empty" or "null" ? 0 : 10);
        if (shape == "null")
            goal = goal with { Tasks = goal.Tasks.Select(task => task with
                { VerificationHistory = null, DispatchHistory = null }).ToArray() };
        await fixture.Repository.SaveGoalSnapshotsAsync([goal]);
        if (shape == "cap")
        {
            goal = AgentOrchestratorKernel.FromSnapshot(new([goal], [])).ExportGoalSnapshot(new(goal.Id));
            Assert.All(goal.Tasks, task =>
            {
                Assert.Equal(20, task.VerificationHistory!.Count);
                Assert.Equal(21, task.DispatchHistory!.Count);
            });
            await fixture.Repository.SaveGoalSnapshotsAsync([goal]);
        }
        if (shape == "removed")
        {
            goal = goal with { Tasks = [goal.Tasks[0]] };
            await fixture.Repository.SaveGoalSnapshotsAsync([goal]);
        }
        await fixture.AssertMirroredAsync(goal);
        var document = fixture.RawGoal(goal.Id);
        var rows = fixture.HistoryRows();
        var loaded = (await fixture.Repository.LoadGoalAsync(new(goal.Id)))!;
        Assert.Equal(GoalTaskHistoryTestFixture.Json(goal), GoalTaskHistoryTestFixture.Json(loaded));
        Assert.Equal(goal.Tasks.Sum(task => (task.VerificationHistory?.Count ?? 0) + (task.DispatchHistory?.Count ?? 0)), rows.Length);
        await fixture.Repository.SaveGoalSnapshotsAsync([loaded]);
        Assert.Equal(document, fixture.RawGoal(goal.Id));
        Assert.Equal(rows, fixture.HistoryRows());
    }

    [Fact]
    public async Task UnmarkedInlineLoadPreservesValuesAndDoesNotRewriteDocument()
    {
        using var fixture = new GoalTaskHistoryTestFixture();
        var goal = GoalTaskHistoryTestFixture.Snapshot();
        fixture.InsertLegacy(goal);
        var document = fixture.RawGoal(goal.Id);
        var version = fixture.Rows("SELECT CAST(version AS TEXT) FROM goals");
        Assert.Equal(GoalTaskHistoryTestFixture.Json(goal), GoalTaskHistoryTestFixture.Json(
            await fixture.Repository.LoadGoalAsync(new(goal.Id))));
        Assert.Equal(document, fixture.RawGoal(goal.Id));
        Assert.Equal(version, fixture.Rows("SELECT CAST(version AS TEXT) FROM goals"));
        Assert.Empty(fixture.HistoryRows());
    }
}
