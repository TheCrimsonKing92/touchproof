using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class GoalLeanSaveSafetyTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TickCheckpointPreservesStoredRowsAndPersistsInProcessAppends(bool advanceVersion)
    {
        using var support = new GoalTimelineTestSupport();
        var seed = GoalTimelineTestSupport.Snapshot();
        // Storage order remains authoritative even when recorded timestamps are out of order.
        seed = seed with { Timeline = [seed.Timeline[1], seed.Timeline[0], seed.Timeline[2]] };
        await support.Repository.SaveGoalSnapshotsAsync([seed]);
        var id = new GoalId(seed.Id);
        var history = support.Fixture.HistoryRows();
        var timeline = support.Rows();
        var kernel = await ReloadLeanAsync(support, id);
        var baselines = new Dictionary<string, GoalSnapshot>();
        ConductLoopTargetLoader.CaptureMissingTickBaselines(baselines, kernel);
        Assert.Empty(baselines[id.Value].Timeline);
        Assert.All(baselines[id.Value].Tasks, task => Assert.Empty(task.VerificationHistory!));

        var goal = kernel.GetGoal(id);
        AppendVerificationAndEvent(goal);
        if (advanceVersion)
        {
            await support.Repository.TransactGoalAsync(id, (stored, _) => Task.FromResult(
                (true, stored! with { SourceBacklogItemId = "concurrent-writer" }, true)));
        }

        var request = Assert.Single(CliPersistentStateRunner.BuildConductLoopCheckpointRequests(
            kernel, [id], baselines, support.Repository, support.Fixture.DatabasePath,
            containTransientBaselineLoads: false, out var holds));
        Assert.Empty(holds);
        var save = Assert.Single(await support.Repository.SaveGoalSnapshotsWithMergeAsync([request]));
        Assert.Equal(advanceVersion ? GoalSnapshotSaveDisposition.Merged : GoalSnapshotSaveDisposition.Saved,
            save.Disposition);
        Assert.Null(save.PersistedSnapshot!.TickRowVersion);
        if (advanceVersion) Assert.Equal("concurrent-writer", save.PersistedSnapshot.SourceBacklogItemId);
        await AssertRowsWithAppendsAsync(support, seed, history, timeline);
    }

    [Fact]
    public async Task CompareAndSwapPreservesLeanRowsAndPersistsAppends()
    {
        using var support = new GoalTimelineTestSupport();
        var seed = GoalTimelineTestSupport.Snapshot();
        await support.Repository.SaveGoalSnapshotsAsync([seed]);
        var id = new GoalId(seed.Id);
        var history = support.Fixture.HistoryRows();
        var timeline = support.Rows();
        var kernel = await ReloadLeanAsync(support, id);
        AppendVerificationAndEvent(kernel.GetGoal(id));
        var current = Assert.Single(OrchestratorSnapshot.Capture(kernel, includeDeferredRows: false).Goals);

        await support.Repository.TransactGoalAsync(id, (stored, _) =>
        {
            Assert.Equal(seed.Timeline.Count, stored!.Timeline.Count);
            Assert.Equal(seed.Tasks[1].DispatchHistory!.Count, stored.Tasks[1].DispatchHistory!.Count);
            return Task.FromResult<(bool, GoalSnapshot?, bool)>((true, current, true));
        });

        await AssertRowsWithAppendsAsync(support, seed, history, timeline);
    }

    [Fact]
    public async Task CompareAndSwapRejectsAStaleVersionAndLeavesRowsIntact()
    {
        using var support = new GoalTimelineTestSupport();
        var seed = GoalTimelineTestSupport.Snapshot();
        await support.Repository.SaveGoalSnapshotsAsync([seed]);
        var id = new GoalId(seed.Id);
        var kernel = await ReloadLeanAsync(support, id);
        AppendVerificationAndEvent(kernel.GetGoal(id));
        var current = Assert.Single(OrchestratorSnapshot.Capture(kernel, includeDeferredRows: false).Goals);
        var history = support.Fixture.HistoryRows();
        var timeline = support.Rows();

        await Assert.ThrowsAsync<GoalTransactionConflictException>(() => support.Repository.TransactGoalAsync(
            id, (_, _) =>
            {
                support.Fixture.Execute("UPDATE goals SET version = version + 1");
                return Task.FromResult<(bool, GoalSnapshot?, bool)>((true, current, true));
            }));

        Assert.Equal(history, support.Fixture.HistoryRows());
        Assert.Equal(timeline, support.Rows());
    }

    private static async Task<AgentOrchestratorKernel> ReloadLeanAsync(GoalTimelineTestSupport support, GoalId id)
    {
        var memory = new GoalReloadMemory();
        var load = await support.Repository.LoadGoalsReusingUnchangedAsync([id], memory);
        Assert.Empty(memory.Entries[id.Value].Snapshot.Timeline);
        return load.Kernel;
    }

    private static void AppendVerificationAndEvent(Goal goal)
    {
        var at = GoalTimelineTestSupport.At.AddHours(1);
        goal.Tasks[0].RecordVerification(new("verify-tick", "fixture", 1, "tick verification", "", at));
        goal.Append(new(goal.Id, goal.Tasks[0].Id, ProgressKind.TaskNote, "tick event", at));
    }

    private static async Task AssertRowsWithAppendsAsync(GoalTimelineTestSupport support, GoalSnapshot seed,
        string[] originalHistory, string[] originalTimeline)
    {
        var saved = (await support.Repository.LoadGoalAsync(new(seed.Id)))!;
        var task = saved.Tasks[0];
        Assert.Equal(seed.Tasks[0].VerificationHistory!.Count + 1, task.VerificationHistory!.Count);
        var newVerification = task.VerificationHistory[^1];
        Assert.Equal("tick verification", newVerification.StandardOutput);
        var newHistoryRow = $"{seed.Id}|{task.Id}|verification|{seed.Tasks[0].VerificationHistory!.Count}|"
            + GoalTimelineTestSupport.Json(newVerification);
        var history = support.Fixture.HistoryRows();
        Assert.Equal(newHistoryRow, Assert.Single(history.Except(originalHistory, StringComparer.Ordinal)));
        Assert.Equal(originalHistory, history.Where(row => row != newHistoryRow));
        var newEvent = saved.Timeline[^1];
        Assert.Equal("tick event", newEvent.Message);
        Assert.Equal(originalTimeline.Append($"{seed.Id}|{seed.Timeline.Count}|{GoalTimelineTestSupport.Json(newEvent)}"),
            support.Rows());
        using var connection = support.Fixture.Open();
        Assert.True(await support.Fixture.Store.IsConsistentAsync(
            connection, seed.Id, support.Fixture.RawGoal(seed.Id)));
    }
}
