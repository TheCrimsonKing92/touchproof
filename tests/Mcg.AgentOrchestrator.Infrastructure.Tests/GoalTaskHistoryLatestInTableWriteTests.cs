using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each write path owns its database.
public sealed class GoalTaskHistoryLatestInTableWriteTests
{
    [Theory]
    [InlineData("snapshots")]
    [InlineData("kernel")]
    [InlineData("cas")]
    [InlineData("state-cas")]
    [InlineData("creation")]
    [InlineData("merge")]
    public async Task EveryWriter_OmitsEqualLatestAndRoundTripsWithoutMutatingCaller(string path)
    {
        using var fixture = new GoalTaskHistoryTestFixture();
        var goal = GoalTaskHistoryTestFixture.LatestSnapshot();
        var before = GoalTaskHistoryTestFixture.Json(goal);
        var id = new GoalId(goal.Id);
        if (path == "snapshots") await fixture.Repository.SaveGoalSnapshotsAsync([goal]);
        else if (path == "kernel")
            await fixture.Repository.SaveAsync(AgentOrchestratorKernel.FromSnapshot(new([goal], [])));
        else if (path == "cas")
            await fixture.Repository.TransactGoalAsync(id, (_, _) => Task.FromResult((true, (GoalSnapshot?)goal, true)));
        else if (path == "state-cas")
            await fixture.Repository.TransactGoalStateWithOutboxAsync("latest-insert", id, (_, _) => Task.FromResult(
                (true, (GoalStateSnapshot?)new GoalStateSnapshot(goal, []), true,
                    (IReadOnlyList<OrchestratorStateOutboxMessage>)[])));
        else if (path == "creation")
            await fixture.Repository.TransactGoalCreationWithOutboxAsync(new GoalCreationLoadScope([], [], []), (kernel, _) =>
            {
                kernel.ReplaceGoalStateWithSnapshot(goal, []);
                return Task.FromResult((true, true, (IReadOnlyList<OrchestratorStateOutboxMessage>)[]));
            });
        else
        {
            var baseline = goal with { Objective = "baseline" };
            await fixture.Repository.SaveGoalSnapshotsAsync([baseline]);
            var result = Assert.Single(await fixture.Repository.SaveGoalSnapshotsWithMergeAsync([new(baseline, goal)]));
            Assert.Equal(GoalSnapshotSaveDisposition.Saved, result.Disposition);
        }
        fixture.AssertLatestRepresentation(goal);
        Assert.Equal(before, GoalTaskHistoryTestFixture.Json(goal));
        Assert.Equal(before, GoalTaskHistoryTestFixture.Json(await fixture.Repository.LoadGoalAsync(id)));
        await fixture.AssertMirroredAsync(goal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task EachKindIsFlaggedIndependentlyUsingSerializedEquality(bool verification)
    {
        using var fixture = new GoalTaskHistoryTestFixture();
        var seed = GoalTaskHistoryTestFixture.Snapshot();
        var goal = seed with { Tasks = seed.Tasks.Select(task => task with
        {
            LastVerification = verification ? task.LastVerification! with { } : task.LastVerification! with { StandardError = "different" },
            LastDispatch = verification ? task.LastDispatch! with { Command = "different" } : task.LastDispatch! with { }
        }).ToArray() };
        await fixture.Repository.SaveGoalSnapshotsAsync([goal]);
        fixture.AssertLatestRepresentation(goal, verification, !verification);
        Assert.Equal(GoalTaskHistoryTestFixture.Json(goal),
            GoalTaskHistoryTestFixture.Json(await fixture.Repository.LoadGoalAsync(new(goal.Id))));
    }
}
