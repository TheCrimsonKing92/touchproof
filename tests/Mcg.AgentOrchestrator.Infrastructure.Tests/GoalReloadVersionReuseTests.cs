using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each case owns its database and marker observer.
public sealed class GoalReloadVersionReuseTests
{
    [Fact]
    public async Task UnchangedGoalsReuseCompleteSnapshotsWithoutSharingKernelMutations()
    {
        using var support = new GoalTimelineTestSupport();
        var (first, second) = await SeedTwoGoalsAsync(support);
        GoalId[] ids = [new(second.Id), new(first.Id)];
        var reads = new List<GoalSnapshotReadObservation>();
        var repository = OpenObserved(support, reads);
        var memory = new GoalReloadMemory();
        var initial = await repository.LoadGoalsReusingUnchangedAsync(ids, memory);
        Assert.Equal((2, 0), (initial.Reloaded, initial.Reused));
        var expected = GoalTimelineTestSupport.Json(initial.Kernel.ExportSnapshot());
        Assert.Equal(expected, GoalTimelineTestSupport.Json((await repository.LoadGoalsAsync(ids)).ExportSnapshot()));

        var firstId = new GoalId(first.Id);
        initial.Kernel.Goals.Single(goal => goal.Id == firstId)
            .Append(new(firstId, null, ProgressKind.TaskNote, "Unsaved tick mutation", GoalTimelineTestSupport.At));
        Assert.NotEqual(expected, GoalTimelineTestSupport.Json(initial.Kernel.ExportSnapshot()));

        reads.Clear();
        var repeated = await repository.LoadGoalsReusingUnchangedAsync(ids, memory);
        Assert.Equal((0, 2), (repeated.Reloaded, repeated.Reused));
        Assert.Equal(expected, GoalTimelineTestSupport.Json(repeated.Kernel.ExportSnapshot()));
        Assert.Equal(new[] { first.Id, second.Id }, GoalIds(reads, "version-reused"));
        Assert.Empty(GoalIds(reads, "hot-attempt"));
        Assert.Empty(GoalIds(reads, "hot-deserialized"));

        // Deliberately violate the writer contract to detect any rehydration of a reused goal.
        // A full reader must still reject each missing set of marked rows.
        support.Fixture.Execute("DELETE FROM goal_task_history");
        await Assert.ThrowsAsync<InvalidDataException>(() => repository.LoadGoalsAsync(ids));
        Assert.Equal(expected, GoalTimelineTestSupport.Json(
            (await repository.LoadGoalsReusingUnchangedAsync(ids, memory)).Kernel.ExportSnapshot()));

        support.Fixture.Execute("DELETE FROM goal_timeline");
        await Assert.ThrowsAsync<InvalidDataException>(() => repository.LoadGoalsAsync(ids));
        Assert.Equal(expected, GoalTimelineTestSupport.Json(
            (await repository.LoadGoalsReusingUnchangedAsync(ids, memory)).Kernel.ExportSnapshot()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StoreWriteRereadsOnlyTheChangedGoal(bool secondRepository)
    {
        using var support = new GoalTimelineTestSupport();
        var (first, second) = await SeedTwoGoalsAsync(support);
        GoalId[] ids = [new(first.Id), new(second.Id)];
        var reads = new List<GoalSnapshotReadObservation>();
        var repository = OpenObserved(support, reads);
        var memory = new GoalReloadMemory();
        await repository.LoadGoalsReusingUnchangedAsync(ids, memory);
        var previousVersion = memory.Entries[first.Id].Version;
        var writer = secondRepository ? support.Repository : repository;
        const string updatedObjective = "Updated in the state store";
        var originalBrief = Assert.Single(first.BriefVersions!);
        var changed = first with
        {
            Objective = updatedObjective,
            BriefVersions = [originalBrief with { Text = updatedObjective }]
        };
        await writer.SaveGoalSnapshotsAsync([changed]);

        reads.Clear();
        var result = await repository.LoadGoalsReusingUnchangedAsync(ids, memory);
        Assert.Equal((1, 1), (result.Reloaded, result.Reused));
        Assert.Equal(first.Id, Assert.Single(GoalIds(reads, "hot-attempt")));
        Assert.Equal(first.Id, Assert.Single(GoalIds(reads, "hot-deserialized")));
        Assert.Equal(second.Id, Assert.Single(GoalIds(reads, "version-reused")));
        Assert.Equal(GoalTimelineTestSupport.Json(changed),
            GoalTimelineTestSupport.Json(result.Kernel.ExportGoalSnapshot(ids[0])));
        Assert.Equal(GoalTimelineTestSupport.Json(second),
            GoalTimelineTestSupport.Json(result.Kernel.ExportGoalSnapshot(ids[1])));
        var currentVersion = await SqliteOrchestratorStateRepository.TryLoadGoalStateVersionAsync(
            support.Fixture.DatabasePath, first.Id);
        Assert.Equal<long?>(previousVersion + 1, currentVersion);
        Assert.Equal(currentVersion, memory.Entries[first.Id].Version);
    }

    [Fact]
    public async Task ConductorMergeSaveInvalidatesItsOwnRememberedSnapshot()
    {
        using var support = new GoalTimelineTestSupport();
        var goal = GoalTimelineTestSupport.Snapshot();
        await support.Repository.SaveGoalSnapshotsAsync([goal]);
        var id = new GoalId(goal.Id);
        var reads = new List<GoalSnapshotReadObservation>();
        var repository = OpenObserved(support, reads);
        var memory = new GoalReloadMemory();
        var initial = await repository.LoadGoalsReusingUnchangedAsync([id], memory);
        var baseline = initial.Kernel.ExportGoalSnapshot(id);
        initial.Kernel.Goals.Single().Append(new(id, null, ProgressKind.TaskNote, "Saved tick mutation",
            GoalTimelineTestSupport.At.AddMinutes(1)));
        var changed = initial.Kernel.ExportGoalSnapshot(id);
        Assert.NotEqual(GoalTimelineTestSupport.Json(baseline), GoalTimelineTestSupport.Json(changed));
        var save = Assert.Single(await repository.SaveGoalSnapshotsWithMergeAsync([new(baseline, changed)]));
        Assert.NotEqual(GoalSnapshotSaveDisposition.Skipped, save.Disposition);

        reads.Clear();
        var result = await repository.LoadGoalsReusingUnchangedAsync([id], memory);
        Assert.Equal((1, 0), (result.Reloaded, result.Reused));
        Assert.Equal(goal.Id, Assert.Single(GoalIds(reads, "hot-attempt")));
        Assert.Empty(GoalIds(reads, "version-reused"));
        Assert.Equal(GoalTimelineTestSupport.Json(changed), GoalTimelineTestSupport.Json(result.Kernel.ExportGoalSnapshot(id)));
        var storedVersion = await SqliteOrchestratorStateRepository.TryLoadGoalStateVersionAsync(
            support.Fixture.DatabasePath, goal.Id);
        Assert.Equal(storedVersion, memory.Entries[goal.Id].Version);
    }

    private static async Task<(GoalSnapshot First, GoalSnapshot Second)> SeedTwoGoalsAsync(GoalTimelineTestSupport support)
    {
        var first = GoalTimelineTestSupport.Snapshot();
        const string secondId = "bbbbbbbb000000000000000000000000";
        var second = first with { Id = secondId, Timeline = first.Timeline.Select(e => e with { GoalId = secondId }).ToArray() };
        await support.Repository.SaveGoalSnapshotsAsync([first, second]);
        support.Fixture.MarkStoredHistory(first.Id);
        support.Fixture.MarkStoredHistory(secondId);
        var firstId = new GoalId(first.Id);
        var id = new GoalId(secondId);
        var loaded = await support.Repository.LoadGoalsAsync([firstId, id]);
        return (loaded.ExportGoalSnapshot(firstId), loaded.ExportGoalSnapshot(id));
    }

    private static SqliteOrchestratorStateRepository OpenObserved(
        GoalTimelineTestSupport support, List<GoalSnapshotReadObservation> reads) =>
        new(support.Fixture.DatabasePath, statementObserver: null, telemetryOptions: null, readObserver: reads.Add);

    private static string?[] GoalIds(IEnumerable<GoalSnapshotReadObservation> reads, string operation) =>
        reads.Where(read => read.Operation == operation).Select(read => read.GoalId).Order(StringComparer.Ordinal).ToArray();
}
