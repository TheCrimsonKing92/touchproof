using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each database and cache is private to the test.
public sealed class GoalTaskHistoryReaderParityTests
{
    [Fact]
    public async Task AllInventoriedReadersAgreeForMarkedAndInlineHistory()
    {
        using var fixture = new GoalTaskHistoryTestFixture();
        using var legacy = new GoalTaskHistoryTestFixture();
        var goal = GoalTaskHistoryTestFixture.Snapshot();
        await fixture.Repository.SaveGoalSnapshotsAsync([goal]);
        using var document = JsonDocument.Parse(fixture.RawGoal(goal.Id));
        using (var connection = fixture.Open())
        {
            var history = await fixture.Store.ReadAsync(connection, goal.Id, document.RootElement.GetProperty("Tasks")[0]);
            Assert.True(history.Verification.FromTable);
            Assert.True(history.Dispatch.FromTable);
        }
        var table = await ReadReadersAsync(fixture, goal);
        legacy.InsertLegacy(goal);
        using (var connection = legacy.Open())
        using (var inline = JsonDocument.Parse(legacy.RawGoal(goal.Id)))
        {
            var history = await legacy.Store.ReadAsync(connection, goal.Id, inline.RootElement.GetProperty("Tasks")[0]);
            Assert.False(history.Verification.FromTable);
            Assert.False(history.Dispatch.FromTable);
        }
        Assert.Equal(table, await ReadReadersAsync(legacy, goal));
        Assert.Equal(GoalTaskHistoryTestFixture.Json(goal), legacy.RawGoal(goal.Id));
    }

    internal static async Task<string> ReadReadersAsync(GoalTaskHistoryTestFixture fixture, GoalSnapshot goal)
    {
        var id = new GoalId(goal.Id);
        var single = await fixture.Repository.LoadGoalAsync(id);
        var full = (await fixture.Repository.LoadAsync()).ExportGoalSnapshot(id);
        var selected = (await fixture.Repository.LoadGoalsAsync([id])).ExportGoalSnapshot(id);
        var prefix = Assert.Single(await fixture.Repository.FindGoalSnapshotsByIdPrefixAsync(goal.Id[..8], 1));
        var taskPrefix = Assert.Single(await fixture.Repository.FindTaskSnapshotsByIdPrefixAsync(goal.Tasks[0].Id, 1));
        var canonical = AgentOrchestratorKernel.FromSnapshot(new([goal], [])).ExportGoalSnapshot(id);
        foreach (var (actual, expected) in new[] { (single!, goal), (full, canonical), (selected, canonical), (prefix, goal) })
        {
            Assert.Equal(GoalTaskHistoryTestFixture.Json(expected.Tasks.Select(task => task.VerificationHistory)),
                GoalTaskHistoryTestFixture.Json(actual.Tasks.Select(task => task.VerificationHistory)));
            Assert.Equal(GoalTaskHistoryTestFixture.Json(expected.Tasks.Select(task => task.DispatchHistory)),
                GoalTaskHistoryTestFixture.Json(actual.Tasks.Select(task => task.DispatchHistory)));
        }
        var attention = await fixture.Repository.ReadAttentionProjectionAsync();
        var backlog = await fixture.Repository.ListCompletedGoalSimilarityDocumentsAsync();
        using var connection = fixture.Open();
        var seed = await ProviderHoldSeedEvidenceReader.ReadAsync(connection, [new GoalId(goal.Id)],
            GoalTaskHistoryTestFixture.Options, default);
        var cached = await ProviderHoldSeedEvidenceStore.ReadAsync(connection, [new GoalId(goal.Id)],
            GoalTaskHistoryTestFixture.Options, default);
        var cacheHit = await ProviderHoldSeedEvidenceStore.ReadAsync(connection, [new GoalId(goal.Id)],
            GoalTaskHistoryTestFixture.Options, default);
        var inline = await ProviderHoldSeedEvidenceReader.ProjectAsync(goal.Id, GoalTaskHistoryTestFixture.Json(goal),
            GoalTaskHistoryTestFixture.Options);
        Assert.Equal(GoalTaskHistoryTestFixture.Json(new[] { inline }), GoalTaskHistoryTestFixture.Json(seed));
        Assert.Equal(GoalTaskHistoryTestFixture.Json(seed), GoalTaskHistoryTestFixture.Json(cached));
        Assert.Equal(GoalTaskHistoryTestFixture.Json(seed), GoalTaskHistoryTestFixture.Json(cacheHit));
        Assert.Single(backlog);
        var epic = Assert.Single(await SqliteOrchestratorStateRepository.ReadEpicGoalsAsync(connection, [goal.Id]));
        Assert.Equal(goal.Status, epic.Status);
        return GoalTaskHistoryTestFixture.Json(new { single, full, selected, prefix, taskPrefix,
            attention, backlog, seed, cached, epicStatus = epic.Status, epicTimeline = epic.Timeline });
    }
}
