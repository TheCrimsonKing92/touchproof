using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each test owns its database and provider evidence cache.
public sealed class GoalTaskHistoryMarkedReadTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MarkedTask_AllReadPaths_UseOrderedTableHistory(bool staleInline)
    {
        using var fixture = new GoalTaskHistoryTestFixture();
        var goal = GoalTaskHistoryTestFixture.Snapshot();
        // Distinct task values also detect accidental cross-task hydration.
        goal = goal with { Tasks = goal.Tasks.Select(task =>
        {
            var verification = task.VerificationHistory!.Select(entry => entry with
                { StandardOutput = task.Id + entry.StandardOutput }).ToArray();
            var dispatch = task.DispatchHistory!.Select(entry => entry with
                { Command = task.Id + entry.Command }).ToArray();
            return task with { VerificationHistory = verification, LastVerification = verification[^1],
                DispatchHistory = dispatch, LastDispatch = dispatch[^1] };
        }).ToArray() };
        goal = AgentOrchestratorKernel.FromSnapshot(new([goal], [])).ExportGoalSnapshot(new(goal.Id));
        await fixture.Repository.SaveGoalSnapshotsAsync([goal]);
        using var legacy = new GoalTaskHistoryTestFixture();
        legacy.InsertLegacy(goal);
        Assert.Equal(await ReadPathsAsync(legacy, goal), await ReadPathsAsync(fixture, goal));
        var before = await ReadPathsAsync(fixture, goal);
        fixture.MarkStoredHistory(goal.Id, staleInline);
        if (staleInline)
            fixture.Execute("UPDATE goals SET snapshot_json = json_set(snapshot_json, '$.Tasks[0].VerificationHistory', json('[]'), '$.Tasks[0].DispatchHistory', json('[]'))");
        Assert.Equal(before, await ReadPathsAsync(fixture, goal));
        // Reads never migrate or rewrite the marked row.
        Assert.Contains("\"HistoryInTable\"", fixture.RawGoal(goal.Id));
    }

    [Fact]
    public async Task ArchivedMarkedGoal_SelectedReaders_RecoverBothLists()
    {
        using var fixture = new GoalTaskHistoryTestFixture();
        var goal = GoalTaskHistoryTestFixture.Snapshot();
        // Canonicalize empty invocation lists: JSON null is treated as open build custody
        // by the archive eligibility query, which would skip this terminal goal.
        goal = AgentOrchestratorKernel.FromSnapshot(new([goal], [])).ExportGoalSnapshot(new(goal.Id));
        await fixture.Repository.SaveGoalSnapshotsAsync([goal]);
        var expected = GoalTaskHistoryTestFixture.Json(await fixture.Repository.LoadGoalAsync(new(goal.Id)));
        fixture.MarkStoredHistory(goal.Id);
        Assert.Equal(1, await fixture.Repository.ArchiveTerminalGoalSnapshotsAsync(GoalTaskHistoryTestFixture.At.AddDays(1)));
        Assert.Contains("\"IsMetadataOnly\":true", fixture.RawGoal(goal.Id));
        Assert.Equal(expected, GoalTaskHistoryTestFixture.Json(await fixture.Repository.LoadGoalAsync(new(goal.Id))));
        Assert.Equal(expected, GoalTaskHistoryTestFixture.Json(Assert.Single(
            await fixture.Repository.FindGoalSnapshotsByIdPrefixAsync(goal.Id[..8], 1))));
        var selected = await fixture.Repository.LoadGoalsAsync([new(goal.Id)]);
        Assert.Equal(10, selected.ExportGoalSnapshot(new(goal.Id)).Tasks[0].VerificationHistory!.Count);
        using var connection = fixture.Open();
        var evidence = Assert.Single(await ProviderHoldSeedEvidenceReader.ReadAsync(connection, [new(goal.Id)],
            GoalTaskHistoryTestFixture.Options, default));
        Assert.All(evidence.Tasks, task =>
        {
            Assert.Equal(10, task.VerificationHistory!.Count);
            Assert.Equal(10, task.DispatchHistory!.Count);
        });
        Assert.Single(await fixture.Repository.ListCompletedGoalSimilarityDocumentsAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MarkedEmptyLists_PreserveNullVersusEmpty(bool nullLists)
    {
        using var fixture = new GoalTaskHistoryTestFixture();
        var seed = GoalTaskHistoryTestFixture.Snapshot(entries: 0);
        var goal = seed with { Tasks = seed.Tasks.Select(task => task with
            { VerificationHistory = nullLists ? null : [], DispatchHistory = nullLists ? null : [] }).ToArray() };
        await fixture.Repository.SaveGoalSnapshotsAsync([goal]);
        fixture.MarkStoredHistory(goal.Id);
        Assert.Equal(GoalTaskHistoryTestFixture.Json(goal), GoalTaskHistoryTestFixture.Json(
            await fixture.Repository.LoadGoalAsync(new(goal.Id))));
    }

    private static async Task<string> ReadPathsAsync(GoalTaskHistoryTestFixture fixture, GoalSnapshot goal)
    {
        var id = new GoalId(goal.Id);
        var full = (await fixture.Repository.LoadAsync()).ExportGoalSnapshot(id);
        var selected = (await fixture.Repository.LoadGoalsAsync([id])).ExportGoalSnapshot(id);
        var single = await fixture.Repository.LoadGoalAsync(id);
        var prefix = Assert.Single(await fixture.Repository.FindGoalSnapshotsByIdPrefixAsync(goal.Id[..8], 1));
        var task = Assert.Single(await fixture.Repository.FindTaskSnapshotsByIdPrefixAsync(goal.Tasks[0].Id, 1));
        var attention = await fixture.Repository.ReadAttentionProjectionAsync();
        var similarity = await fixture.Repository.ListCompletedGoalSimilarityDocumentsAsync();
        using var connection = fixture.Open();
        var evidence = await ProviderHoldSeedEvidenceReader.ReadAsync(connection, [id], GoalTaskHistoryTestFixture.Options, default);
        Assert.All(new[] { full, selected, single!, prefix }, actual =>
        {
            Assert.Equal(GoalTaskHistoryTestFixture.Json(goal.Tasks.Select(t => t.VerificationHistory)),
                GoalTaskHistoryTestFixture.Json(actual.Tasks.Select(t => t.VerificationHistory)));
            Assert.Equal(GoalTaskHistoryTestFixture.Json(goal.Tasks.Select(t => t.DispatchHistory)),
                GoalTaskHistoryTestFixture.Json(actual.Tasks.Select(t => t.DispatchHistory)));
        });
        Assert.Single(similarity);
        Assert.Equal(2, Assert.Single(evidence).Tasks.Count);
        return GoalTaskHistoryTestFixture.Json(new { full, selected, single, prefix, task, attention, similarity, evidence });
    }
}
