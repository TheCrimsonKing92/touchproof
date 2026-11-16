using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each save path owns its database.
public sealed class GoalTimelineInTableSaveTests
{
    [Theory]
    [InlineData("snapshots")]
    [InlineData("kernel")]
    [InlineData("cas")]
    [InlineData("state-cas")]
    [InlineData("merge")]
    [InlineData("creation")]
    public async Task EveryWriter_MarksExactRowsAndRoundTripsWithoutMutatingCaller(string path)
    {
        using var support = new GoalTimelineTestSupport();
        var goal = GoalTimelineTestSupport.Snapshot();
        var before = GoalTimelineTestSupport.Json(goal);
        var id = new GoalId(goal.Id);
        switch (path)
        {
            case "snapshots": await support.Repository.SaveGoalSnapshotsAsync([goal]); break;
            case "kernel": await support.Repository.SaveAsync(AgentOrchestratorKernel.FromSnapshot(new([goal], []))); break;
            case "cas": await support.Repository.TransactGoalAsync(id, (_, _) => Task.FromResult((true, (GoalSnapshot?)goal, true))); break;
            case "state-cas":
                await support.Repository.TransactGoalStateWithOutboxAsync("timeline-insert", id, (_, _) => Task.FromResult(
                    (true, (GoalStateSnapshot?)new GoalStateSnapshot(goal, []), true,
                        (IReadOnlyList<OrchestratorStateOutboxMessage>)[])));
                break;
            case "creation":
                await support.Repository.TransactGoalCreationWithOutboxAsync(new GoalCreationLoadScope([], [], []), (kernel, _) =>
                {
                    kernel.ReplaceGoalStateWithSnapshot(goal, []);
                    return Task.FromResult((true, true, (IReadOnlyList<OrchestratorStateOutboxMessage>)[]));
                });
                break;
            default:
                var baseline = goal with { Objective = "baseline" };
                await support.Repository.SaveGoalSnapshotsAsync([baseline]);
                Assert.Equal(GoalSnapshotSaveDisposition.Saved,
                    Assert.Single(await support.Repository.SaveGoalSnapshotsWithMergeAsync([new(baseline, goal)])).Disposition);
                break;
        }
        Assert.Equal(before, GoalTimelineTestSupport.Json(goal));
        await support.AssertMarkedAsync(goal);
    }

    [Theory]
    [InlineData("snapshots")]
    [InlineData("kernel")]
    [InlineData("cas")]
    [InlineData("state-cas")]
    [InlineData("merge")]
    public async Task SaveAfterMarkedLoad_PreservesMarkerRowsAndTimeline(string path)
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
        await support.AssertMarkedAsync(goal);
        Assert.Equal(rows, support.Rows());
        Assert.Equal(GoalTimelineTestSupport.Json(goal), GoalTimelineTestSupport.Json(await support.Repository.LoadGoalAsync(id)));
    }
}
