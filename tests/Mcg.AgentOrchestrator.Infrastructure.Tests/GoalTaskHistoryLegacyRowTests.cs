using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

// Parallel-safe: each test uses a separate SQLite store, including schema changes.
public sealed class GoalTaskHistoryLegacyRowTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DirectlyInsertedLegacyRowPreservesLoadsAndAllReaders(bool missingTable)
    {
        using var fixture = new GoalTaskHistoryTestFixture();
        var goal = GoalTaskHistoryTestFixture.Snapshot();
        fixture.InsertLegacy(goal);
        var expected = await GoalTaskHistoryReaderParityTests.ReadReadersAsync(fixture, goal);
        fixture.Execute("DELETE FROM provider_hold_seed_evidence");
        if (missingTable) fixture.Execute("DROP TABLE goal_task_history");
        Assert.Equal(expected, await GoalTaskHistoryReaderParityTests.ReadReadersAsync(fixture, goal));
        var readOnly = SqliteOrchestratorStateRepository.OpenReadOnly(fixture.DatabasePath);
        Assert.Equal(GoalTaskHistoryTestFixture.Json(goal), GoalTaskHistoryTestFixture.Json(
            await readOnly.LoadGoalAsync(new GoalId(goal.Id))));
        var expectedKernel = AgentOrchestratorKernel.FromSnapshot(new([goal], []));
        Assert.Equal(GoalTaskHistoryTestFixture.Json(expectedKernel.ExportSnapshot()),
            GoalTaskHistoryTestFixture.Json((await readOnly.LoadAsync()).ExportSnapshot()));
        Assert.Equal(GoalTaskHistoryTestFixture.Json(expectedKernel.ExportSnapshot()),
            GoalTaskHistoryTestFixture.Json((await readOnly.LoadGoalsAsync([new GoalId(goal.Id)])).ExportSnapshot()));
    }

    [Theory]
    [InlineData("incomplete")]
    [InlineData("revision")]
    [InlineData("gap")]
    [InlineData("surplus")]
    [InlineData("corrupt")]
    public async Task UnusableMirrorCannotOverrideInlineHistory(string defect)
    {
        using var fixture = new GoalTaskHistoryTestFixture();
        var goal = GoalTaskHistoryTestFixture.Snapshot();
        await fixture.Repository.SaveGoalSnapshotsAsync([goal]);
        fixture.ReplaceDocument(goal.Id, GoalTaskHistoryTestFixture.Json(goal));
        var expectedReaders = await GoalTaskHistoryReaderParityTests.ReadReadersAsync(fixture, goal);
        var predicate = "WHERE kind = 'verification' AND task_id = '00000000000000000000000000000001'";
        fixture.Execute(defect switch
        {
            "incomplete" => "DELETE FROM goal_task_history " + predicate + " AND sequence = 9",
            "revision" => "UPDATE goal_task_history SET entry_json = '{}' " + predicate + " AND sequence = 0",
            "gap" => "UPDATE goal_task_history SET sequence = 12 " + predicate + " AND sequence = 9",
            "surplus" => "INSERT INTO goal_task_history SELECT goal_id, task_id, kind, 10, entry_json FROM goal_task_history " + predicate + " AND sequence = 9",
            _ => "UPDATE goal_task_history SET entry_json = 'not-json' " + predicate + " AND sequence = 0"
        });
        using var connection = fixture.Open();
        using var document = JsonDocument.Parse(fixture.RawGoal(goal.Id));
        var history = await fixture.Store.ReadAsync(connection, goal.Id, document.RootElement.GetProperty("Tasks")[0]);
        Assert.False(history.Verification.FromTable);
        Assert.True(history.Dispatch.FromTable);
        Assert.False(await fixture.Store.IsConsistentAsync(connection, goal.Id, fixture.RawGoal(goal.Id)));
        Assert.Equal(GoalTaskHistoryTestFixture.Json(goal), GoalTaskHistoryTestFixture.Json(
            await fixture.Repository.LoadGoalAsync(new GoalId(goal.Id))));
        fixture.Execute("DELETE FROM provider_hold_seed_evidence");
        Assert.Equal(expectedReaders, await GoalTaskHistoryReaderParityTests.ReadReadersAsync(fixture, goal));
    }

    [Fact]
    public async Task OlderWriterChangingOnlyInlineDocumentWinsOverMirror()
    {
        using var fixture = new GoalTaskHistoryTestFixture();
        var goal = GoalTaskHistoryTestFixture.Snapshot();
        await fixture.Repository.SaveGoalSnapshotsAsync([goal]);
        var task = goal.Tasks[0] with { VerificationHistory = [], DispatchHistory = null };
        goal = goal with { Tasks = [task] };
        using (var connection = fixture.Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE goals SET snapshot_json = $json, version = version + 1";
            command.Parameters.AddWithValue("$json", GoalTaskHistoryTestFixture.Json(goal));
            command.ExecuteNonQuery();
        }
        Assert.Equal(GoalTaskHistoryTestFixture.Json(goal), GoalTaskHistoryTestFixture.Json(
            await fixture.Repository.LoadGoalAsync(new GoalId(goal.Id))));
        await GoalTaskHistoryReaderParityTests.ReadReadersAsync(fixture, goal);
        await fixture.Repository.SaveGoalSnapshotsAsync([goal]);
        await fixture.AssertMirroredAsync(goal);
    }

    [Fact]
    public async Task ArchivedGoalHydratesHistoryBeforeReturningTasks()
    {
        using var fixture = new GoalTaskHistoryTestFixture();
        var seed = GoalTaskHistoryTestFixture.Snapshot();
        // Match normal persisted domain state, including empty worker build invocation lists.
        var goal = AgentOrchestratorKernel.FromSnapshot(new([seed], [])).ExportGoalSnapshot(new GoalId(seed.Id));
        await fixture.Repository.SaveGoalSnapshotsAsync([goal]);
        Assert.Equal(1, await fixture.Repository.ArchiveTerminalGoalSnapshotsAsync(GoalTaskHistoryTestFixture.At.AddDays(1)));
        Assert.Contains("\"IsMetadataOnly\":true", fixture.RawGoal(goal.Id));
        Assert.Equal(GoalTaskHistoryTestFixture.Json(goal), GoalTaskHistoryTestFixture.Json(
            await fixture.Repository.LoadGoalAsync(new GoalId(goal.Id))));
        var historical = Assert.Single(await fixture.Repository.FindGoalSnapshotsByIdPrefixAsync(goal.Id[..8], 1));
        Assert.Equal(GoalTaskHistoryTestFixture.Json(goal), GoalTaskHistoryTestFixture.Json(historical));
        var expected = AgentOrchestratorKernel.FromSnapshot(new([goal], [])).ExportSnapshot();
        Assert.Equal(GoalTaskHistoryTestFixture.Json(expected), GoalTaskHistoryTestFixture.Json(
            (await fixture.Repository.LoadGoalsAsync([new GoalId(goal.Id)])).ExportSnapshot()));
    }

    [Fact]
    public async Task UnrelatedSqliteFailurePropagates()
    {
        using var fixture = new GoalTaskHistoryTestFixture();
        fixture.Execute("DROP TABLE goal_task_history; CREATE TABLE goal_task_history (wrong TEXT)");
        using var connection = fixture.Open();
        using var document = JsonDocument.Parse(GoalTaskHistoryTestFixture.Json(GoalTaskHistoryTestFixture.Snapshot()));
        var error = await Assert.ThrowsAsync<SqliteException>(() => fixture.Store.ReadAsync(connection,
            document.RootElement.GetProperty("Id").GetString()!, document.RootElement.GetProperty("Tasks")[0]));
        Assert.Contains("no such column", error.Message);
    }
}
