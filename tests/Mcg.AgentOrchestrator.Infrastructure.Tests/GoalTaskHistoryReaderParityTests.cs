using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each database and cache is private to the test.
public sealed class GoalTaskHistoryReaderParityTests
{
    [Fact]
    public async Task AllFourReadersAgreeForTableAndInlineHistory()
    {
        using var fixture = new GoalTaskHistoryTestFixture();
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
        fixture.Execute("DELETE FROM goal_task_history; DELETE FROM provider_hold_seed_evidence");
        using (var connection = fixture.Open())
        {
            var history = await fixture.Store.ReadAsync(connection, goal.Id, document.RootElement.GetProperty("Tasks")[0]);
            Assert.False(history.Verification.FromTable);
            Assert.False(history.Dispatch.FromTable);
        }
        Assert.Equal(table, await ReadReadersAsync(fixture, goal));
    }

    internal static async Task<string> ReadReadersAsync(GoalTaskHistoryTestFixture fixture, GoalSnapshot goal)
    {
        var attention = await fixture.Repository.ReadAttentionProjectionAsync();
        var backlog = await fixture.Repository.ListCompletedGoalSimilarityDocumentsAsync();
        using var connection = fixture.Open();
        var seed = await ProviderHoldSeedEvidenceReader.ReadAsync(connection, [new GoalId(goal.Id)],
            GoalTaskHistoryTestFixture.Options, default);
        var cached = await ProviderHoldSeedEvidenceStore.ReadAsync(connection, [new GoalId(goal.Id)],
            GoalTaskHistoryTestFixture.Options, default);
        var cacheHit = await ProviderHoldSeedEvidenceStore.ReadAsync(connection, [new GoalId(goal.Id)],
            GoalTaskHistoryTestFixture.Options, default);
        var inline = await ProviderHoldSeedEvidenceReader.ProjectAsync(goal.Id, fixture.RawGoal(goal.Id),
            GoalTaskHistoryTestFixture.Options);
        Assert.Equal(GoalTaskHistoryTestFixture.Json(new[] { inline }), GoalTaskHistoryTestFixture.Json(seed));
        Assert.Equal(GoalTaskHistoryTestFixture.Json(seed), GoalTaskHistoryTestFixture.Json(cached));
        Assert.Equal(GoalTaskHistoryTestFixture.Json(seed), GoalTaskHistoryTestFixture.Json(cacheHit));
        Assert.Single(backlog);
        return GoalTaskHistoryTestFixture.Json(new { attention, backlog, seed, cached });
    }
}
