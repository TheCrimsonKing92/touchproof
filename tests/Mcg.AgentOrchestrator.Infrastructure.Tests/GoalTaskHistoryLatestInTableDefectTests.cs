using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: faults are confined to a private database.
public sealed class GoalTaskHistoryLatestInTableDefectTests
{
    [Theory]
    [InlineData("verification", "zero")]
    [InlineData("verification", "null")]
    [InlineData("verification", "missing")]
    [InlineData("verification", "bad-flag")]
    [InlineData("dispatch", "zero")]
    [InlineData("dispatch", "null")]
    [InlineData("dispatch", "missing")]
    [InlineData("dispatch", "bad-flag")]
    public async Task TrueMarker_InvalidCountOrMissingRow_FailsEveryReader(string kind, string defect)
    {
        using var fixture = new GoalTaskHistoryTestFixture();
        var goal = GoalTaskHistoryTestFixture.LatestSnapshot();
        await fixture.Repository.SaveGoalSnapshotsAsync([goal]);
        fixture.MarkStoredLatest(goal.Id);
        var property = kind == "verification" ? "Verification" : "Dispatch";
        if (defect != "bad-flag")
            fixture.Execute($"DELETE FROM goal_task_history WHERE task_id = '{goal.Tasks[0].Id}' AND kind = '{kind}'");
        if (defect is "zero" or "null")
            fixture.Execute($"UPDATE goals SET snapshot_json = json_set(snapshot_json, '$.Tasks[0].HistoryInTable.{property}', {(defect == "zero" ? "0" : "NULL")})");
        if (defect == "bad-flag")
            fixture.Execute($"UPDATE goals SET snapshot_json = json_set(snapshot_json, '$.Tasks[0].HistoryInTable.Latest{property}InTable', 'true')");
        var id = new GoalId(goal.Id);
        using var connection = fixture.Open();
        Func<Task>[] readers =
        [
            async () => { await fixture.Repository.LoadGoalAsync(id); },
            async () => { await fixture.Repository.LoadAsync(); },
            async () => { await fixture.Repository.LoadGoalsAsync([id]); },
            async () => { await fixture.Repository.LoadStatusGoalsAsync([id]); },
            async () => { await fixture.Repository.LoadBoardSnapshotAsync(); },
            async () => { await fixture.Repository.FindGoalSnapshotsByIdPrefixAsync(goal.Id[..8], 1); },
            async () => { await fixture.Repository.FindTaskSnapshotsByIdPrefixAsync(goal.Tasks[0].Id, 1); },
            async () => { await fixture.Repository.TransactGoalAsync(id, (snapshot, _) => Task.FromResult((false, snapshot, true))); },
            async () => { await fixture.Repository.TransactGoalStateAsync(id, (state, _) => Task.FromResult((false, state, true))); },
            async () => { await ProviderHoldSeedEvidenceReader.ReadAsync(connection, [id], GoalTaskHistoryTestFixture.Options, default); },
            async () => { await fixture.Repository.ReadAttentionProjectionAsync(); },
            async () => { await fixture.Repository.ListCompletedGoalSimilarityDocumentsAsync(); }
        ];
        foreach (var read in readers)
        {
            var error = await Assert.ThrowsAsync<InvalidDataException>(read);
            Assert.Contains(goal.Id, error.Message);
            Assert.Contains(goal.Tasks[0].Id, error.Message);
            Assert.Contains(kind, error.Message, StringComparison.OrdinalIgnoreCase);
        }
        Assert.Equal(0, await fixture.Repository.ArchiveTerminalGoalSnapshotsAsync(GoalTaskHistoryTestFixture.At.AddDays(1)));
        Assert.DoesNotContain("\"IsMetadataOnly\":true", fixture.RawGoal(goal.Id));
    }
}
