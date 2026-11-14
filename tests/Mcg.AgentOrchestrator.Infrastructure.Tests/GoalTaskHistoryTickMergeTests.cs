using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: all snapshots and storage belong to the test.
public sealed class GoalTaskHistoryTickMergeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StaleTickMirrorsFinalHistorySelectedByExistingMerge(bool storeReplacesHistory)
    {
        using var fixture = new GoalTaskHistoryTestFixture();
        var seed = GoalTaskHistoryTestFixture.Snapshot();
        var baseline = AgentOrchestratorKernel.FromSnapshot(new([seed], [])).ExportGoalSnapshot(new GoalId(seed.Id));
        await fixture.Repository.SaveGoalSnapshotsAsync([baseline]);
        var replacement = baseline.Tasks[0].VerificationHistory!.Reverse().Take(3).ToArray();
        var stored = baseline with { SourceBacklogItemId = "committed-writer" };
        var current = baseline with { Objective = "tick objective" };
        if (storeReplacesHistory)
            stored = stored with { Tasks = [stored.Tasks[0] with { VerificationHistory = replacement }, stored.Tasks[1]] };
        else
            current = current with { Tasks = [current.Tasks[0] with { VerificationHistory = replacement }, current.Tasks[1]] };
        await fixture.Repository.SaveGoalSnapshotsAsync([stored]);
        var result = Assert.Single(await fixture.Repository.SaveGoalSnapshotsWithMergeAsync(
            [new GoalSnapshotSaveRequest(baseline, current)]));
        Assert.Equal(GoalSnapshotSaveDisposition.Merged, result.Disposition);
        var expected = stored with
        {
            Objective = current.Objective,
            GateTestObligations = [],
            Tasks = [stored.Tasks[0] with { VerificationHistory = replacement }, stored.Tasks[1]]
        };
        Assert.Equal(GoalTaskHistoryTestFixture.Json(expected), GoalTaskHistoryTestFixture.Json(result.PersistedSnapshot));
        await fixture.AssertMirroredAsync(expected);
    }
}
