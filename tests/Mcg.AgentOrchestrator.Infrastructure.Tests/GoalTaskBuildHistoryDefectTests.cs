using Mcg.AgentOrchestrator.Core;

public sealed class GoalTaskBuildHistoryDefectTests
{
    [Theory]
    [InlineData("missing", "expected 10 rows, found 9")]
    [InlineData("gap", "expected sequence 5, found 6")]
    [InlineData("corrupt", "invalid invocation at sequence 5")]
    [InlineData("wrong-type", "invalid invocation at sequence 5")]
    [InlineData("missing-table", "missing goal_task_build_history table")]
    [InlineData("unexpected", "expected 0 rows, found 10")]
    public async Task InvalidMarkedHistoryFailsWithGoalAndTask(string defect, string diagnostic)
    {
        using var fixture = new GoalTaskHistoryTestFixture();
        var goal = GoalTaskBuildHistoryTestSupport.Snapshot(tasks: 1);
        await fixture.Repository.SaveGoalSnapshotsAsync([goal]);
        var taskId = goal.Tasks[0].Id;
        var target = $"goal_id = '{goal.Id}' AND task_id = '{taskId}' AND kind = 'build'";
        fixture.Execute(defect switch
        {
            "missing" => $"DELETE FROM goal_task_build_history WHERE {target} AND sequence = 9",
            "gap" => $"UPDATE goal_task_build_history SET sequence = 20 WHERE {target} AND sequence = 5",
            "corrupt" => $"UPDATE goal_task_build_history SET entry_json = 'broken json' WHERE {target} AND sequence = 5",
            "wrong-type" => $"UPDATE goal_task_build_history SET entry_json = '42' WHERE {target} AND sequence = 5",
            "missing-table" => "DROP TABLE goal_task_build_history",
            _ => "UPDATE goals SET snapshot_json = json_set(snapshot_json, '$.Tasks[0].WorkerBuildInvocationsInTable', 0)"
        });

        var id = new GoalId(goal.Id);
        var repository = fixture.Repository;
        Func<Task>[] readers =
        [
            () => repository.LoadGoalAsync(id),
            () => repository.LoadAsync(),
            () => repository.LoadGoalsAsync([id]),
            () => repository.FindGoalSnapshotsByIdPrefixAsync(goal.Id[..8], 1),
            () => repository.FindTaskSnapshotsByIdPrefixAsync(taskId, 1),
            () => repository.TransactGoalStateAsync(id, (state, _) => Task.FromResult((false, state, state!.Goal)))
        ];
        foreach (var read in readers)
        {
            var error = await Assert.ThrowsAsync<InvalidDataException>(read);
            Assert.Contains(goal.Id, error.Message);
            Assert.Contains(taskId, error.Message);
            Assert.Contains($"build history: {diagnostic}", error.Message);
        }
    }
}
