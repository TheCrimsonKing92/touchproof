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
    public async Task SecondTickCheckpointPersistsEveryNewVerificationOnTheSameLeanGoal()
    {
        using var support = new GoalTimelineTestSupport();
        var seed = GoalTimelineTestSupport.Snapshot();
        await support.Repository.SaveGoalSnapshotsAsync([seed]);
        var id = new GoalId(seed.Id);
        var kernel = await ReloadLeanAsync(support, id);
        var baselines = new Dictionary<string, GoalSnapshot>();
        ConductLoopTargetLoader.CaptureMissingTickBaselines(baselines, kernel);
        Assert.NotNull(baselines[seed.Id].TickRowVersion);

        var goal = kernel.GetGoal(id);
        AppendVerificationAndEvent(goal);
        var firstSave = await SaveCheckpointAsync();
        Assert.Equal(GoalSnapshotSaveDisposition.Saved, firstSave.Disposition);
        Assert.False(CliPersistentStateRunner.RebaseCheckpointAfterDurableSave(kernel, firstSave));
        Assert.Same(goal, kernel.GetGoal(id));
        var firstPersisted = Assert.IsType<GoalSnapshot>(firstSave.PersistedSnapshot);
        Assert.Null(firstPersisted.TickRowVersion);
        baselines[seed.Id] = firstPersisted;

        var at = GoalTimelineTestSupport.At.AddHours(2);
        goal.Tasks[0].RecordVerification(new("verify-second", "fixture", 1, "second verification", "", at));
        goal.Tasks[0].RecordVerification(new("verify-third", "fixture", 1, "third verification", "", at.AddMinutes(1)));
        goal.Append(new(goal.Id, goal.Tasks[0].Id, ProgressKind.TaskNote, "second tick event", at));

        var secondSave = await SaveCheckpointAsync();
        Assert.Equal(GoalSnapshotSaveDisposition.Saved, secondSave.Disposition);
        var stored = Assert.IsType<GoalSnapshot>(await support.Repository.LoadGoalAsync(id));
        Assert.Equal(seed.Tasks[0].VerificationHistory!.Select(row => row.StandardOutput)
                .Concat(["tick verification", "second verification", "third verification"]),
            stored.Tasks[0].VerificationHistory!.Select(row => row.StandardOutput));
        Assert.Equal(seed.Timeline.Select(row => row.Message).Concat(["tick event", "second tick event"]),
            stored.Timeline.Select(row => row.Message));

        async Task<GoalSnapshotSaveResult> SaveCheckpointAsync()
        {
            var request = Assert.Single(CliPersistentStateRunner.BuildConductLoopCheckpointRequests(
                kernel, [id], baselines, support.Repository, support.Fixture.DatabasePath,
                containTransientBaselineLoads: false, out var holds));
            Assert.Empty(holds);
            return Assert.Single(await support.Repository.SaveGoalSnapshotsWithMergeAsync([request]));
        }
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
