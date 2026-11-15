using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: defects are injected only into the test's own database.
public sealed class GoalTaskHistoryMarkedDefectTests
{
    [Theory]
    [InlineData("missing")]
    [InlineData("gap")]
    [InlineData("corrupt")]
    [InlineData("wrong-type")]
    [InlineData("missing-table")]
    [InlineData("bad-marker")]
    public async Task MarkedDefect_AllReaders_FailWithGoalAndTask(string defect)
    {
        using var fixture = new GoalTaskHistoryTestFixture();
        var goal = GoalTaskHistoryTestFixture.Snapshot();
        await fixture.Repository.SaveGoalSnapshotsAsync([goal]);
        fixture.MarkStoredHistory(goal.Id);
        var target = $"goal_id = '{goal.Id}' AND task_id = '{goal.Tasks[0].Id}' AND kind = 'verification'";
        fixture.Execute(defect switch
        {
            "missing" => $"DELETE FROM goal_task_history WHERE {target}",
            "gap" => $"UPDATE goal_task_history SET sequence = 20 WHERE {target} AND sequence = 5",
            "corrupt" => $"UPDATE goal_task_history SET entry_json = 'broken json' WHERE {target} AND sequence = 5",
            "wrong-type" => $"UPDATE goal_task_history SET entry_json = '42' WHERE {target} AND sequence = 5",
            "missing-table" => "DROP TABLE goal_task_history",
            _ => "UPDATE goals SET snapshot_json = json_set(snapshot_json, '$.Tasks[0].HistoryInTable', 42)"
        });
        var id = new GoalId(goal.Id);
        using var connection = fixture.Open();
        Func<Task>[] readers =
        [
            async () => { await fixture.Repository.LoadGoalAsync(id); },
            async () => { await fixture.Repository.LoadAsync(); },
            async () => { await fixture.Repository.LoadGoalsAsync([id]); },
            async () => { await fixture.Repository.FindGoalSnapshotsByIdPrefixAsync(goal.Id[..8], 1); },
            async () => { await fixture.Repository.FindTaskSnapshotsByIdPrefixAsync(goal.Tasks[0].Id, 1); },
            async () => { await ProviderHoldSeedEvidenceReader.ReadAsync(connection, [id], GoalTaskHistoryTestFixture.Options, default); },
            async () => { await fixture.Repository.ReadAttentionProjectionAsync(); },
            async () => { await fixture.Repository.ListCompletedGoalSimilarityDocumentsAsync(); }
        ];
        foreach (var read in readers)
        {
            var error = await Assert.ThrowsAsync<InvalidDataException>(read);
            Assert.Contains(goal.Id, error.Message);
            Assert.Contains(goal.Tasks[0].Id, error.Message);
        }
    }

    [Fact]
    public async Task MarkedTask_NoConnection_FailsWithIdentity()
    {
        using var fixture = new GoalTaskHistoryTestFixture();
        var goal = GoalTaskHistoryTestFixture.Snapshot();
        await fixture.Repository.SaveGoalSnapshotsAsync([goal]);
        fixture.MarkStoredHistory(goal.Id);
        using var document = JsonDocument.Parse(fixture.RawGoal(goal.Id));
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Store.ReadAsync(null, goal.Id,
            document.RootElement.GetProperty("Tasks")[0]));
        Assert.Contains(goal.Id, error.Message);
        Assert.Contains(goal.Tasks[0].Id, error.Message);
    }
}
