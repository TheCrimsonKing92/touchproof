using Mcg.AgentOrchestrator.Core;

// Parallel-safe: each case owns its store and kernel.
public sealed class GoalTaskHistoryMirrorTests
{
    [Fact]
    public async Task DispatchRevisionRewritesItsPosition()
    {
        using var fixture = new GoalTaskHistoryTestFixture();
        var goal = GoalTaskHistoryTestFixture.Snapshot();
        await fixture.Repository.SaveGoalSnapshotsAsync([goal]);
        var task = goal.Tasks[0];
        var dispatches = task.DispatchHistory!.ToArray();
        dispatches[3] = dispatches[3] with { ProviderUsage = DispatchProviderUsage.From(null, "fixture usage revision") };
        goal = goal with { Tasks = [task with { DispatchHistory = dispatches }, goal.Tasks[1]] };
        await fixture.Repository.SaveGoalSnapshotsAsync([goal]);
        await fixture.AssertMirroredAsync(goal);
        Assert.Equal(40, fixture.HistoryRows().Length);
        Assert.Single(fixture.HistoryRows().Where(row => row.Contains("fixture usage revision", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task VerificationCapTrimRewritesShiftedEntries()
    {
        using var fixture = new GoalTaskHistoryTestFixture();
        var goal = GoalTaskHistoryTestFixture.Snapshot(entries: 21, taskCount: 1);
        var verification = goal.Tasks[0].VerificationHistory!.ToArray();
        // A successful Researcher artifact is protected; the cap must remove index 1, not index 0.
        verification[0] = verification[0] with { StandardOutputPath = "fixture-research-proof" };
        goal = goal with { Tasks = [goal.Tasks[0] with
            { RequiredRole = AgentRole.Researcher, VerificationHistory = verification }] };
        await fixture.Repository.SaveGoalSnapshotsAsync([goal]);
        var kernel = AgentOrchestratorKernel.FromSnapshot(new([goal], []));
        var capped = kernel.ExportGoalSnapshot(new GoalId(goal.Id));
        Assert.Equal(20, capped.Tasks[0].VerificationHistory!.Count);
        Assert.Equal(verification[0].CompletedAt, capped.Tasks[0].VerificationHistory![0].CompletedAt);
        Assert.DoesNotContain(capped.Tasks[0].VerificationHistory!, entry => entry.CompletedAt == verification[1].CompletedAt);
        Assert.NotEqual(GoalTaskHistoryTestFixture.Json(goal.Tasks[0].VerificationHistory),
            GoalTaskHistoryTestFixture.Json(capped.Tasks[0].VerificationHistory));
        await fixture.Repository.SaveGoalSnapshotsAsync([capped]);
        await fixture.AssertMirroredAsync(capped);
        Assert.Equal(41, fixture.HistoryRows().Length);
    }

    [Fact]
    public async Task SaveLoadSavePreservesOrderMultiplicityAndRows()
    {
        using var fixture = new GoalTaskHistoryTestFixture();
        var goal = GoalTaskHistoryTestFixture.Snapshot();
        // Equal content in distinct positions must remain two rows.
        var dispatches = goal.Tasks[0].DispatchHistory!.ToArray();
        dispatches[1] = dispatches[0];
        goal = goal with { Tasks = [goal.Tasks[0] with { DispatchHistory = dispatches }, goal.Tasks[1]] };
        await fixture.Repository.SaveGoalSnapshotsAsync([goal]);
        var before = fixture.HistoryRows();
        var loaded = (await fixture.Repository.LoadGoalAsync(new GoalId(goal.Id)))!;
        await fixture.Repository.SaveGoalSnapshotsAsync([loaded]);
        await fixture.AssertMirroredAsync(goal);
        Assert.Equal(before, fixture.HistoryRows());
        Assert.Equal(40, before.Length);
    }

    [Fact]
    public async Task ListReplacementAndRemovedTasksLeaveNoStaleRows()
    {
        using var fixture = new GoalTaskHistoryTestFixture();
        var goal = GoalTaskHistoryTestFixture.Snapshot();
        await fixture.Repository.SaveGoalSnapshotsAsync([goal]);
        var task = goal.Tasks[0] with
        {
            VerificationHistory = goal.Tasks[0].VerificationHistory!.Reverse().Take(3).ToArray(), DispatchHistory = []
        };
        goal = goal with { Tasks = [task] };
        await fixture.Repository.SaveGoalSnapshotsAsync([goal]);
        await fixture.AssertMirroredAsync(goal);
        Assert.Equal(3, fixture.HistoryRows().Length);
        goal = goal with { Tasks = [task with { VerificationHistory = [], DispatchHistory = [] }] };
        await fixture.Repository.SaveGoalSnapshotsAsync([goal]);
        await fixture.AssertMirroredAsync(goal);
        Assert.Empty(fixture.HistoryRows());
    }
}
