using System.Globalization;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;
using SQLitePCL;

// Parallel-safe: every case owns its database, telemetry destination, and traced connection.
public sealed class StateStoreFormatFenceTests
{
    private const string AppliedBefore = "2000-01-01T00:00:00.0000000+00:00";
    private static int CurrentLevel => StoreSchemaRegistry.StateStoreFormat.CurrentVersion!.Value;

    [Theory]
    [InlineData("save")]
    [InlineData("snapshots")]
    [InlineData("state-transaction")]
    [InlineData("creation")]
    public async Task NewerLevel_AllWritersRefuseAndPreserveRows(string writer)
    {
        using var fixture = new GoalTaskHistoryTestFixture();
        var goal = GoalTaskHistoryTestFixture.Snapshot();
        await fixture.Repository.SaveGoalSnapshotsAsync([goal]);
        await fixture.AssertMirroredAsync(goal);
        Assert.Equal(40, fixture.HistoryRows().Length);
        SetLevel(fixture, CurrentLevel + 1);
        var goalsBefore = GoalRows(fixture);
        var historyBefore = HistoryRows(fixture);
        var levelBefore = LevelRow(fixture);
        var changed = goal with { Objective = "This write must be refused" };
        var creationCalled = false;

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            switch (writer)
            {
                case "save":
                    await fixture.Repository.SaveAsync(
                        AgentOrchestratorKernel.FromSnapshot(new([changed], [])));
                    break;
                case "snapshots":
                    await fixture.Repository.SaveGoalSnapshotsAsync([changed]);
                    break;
                case "state-transaction":
                    await fixture.Repository.TransactGoalStateAsync(new(goal.Id), (state, _) =>
                        Task.FromResult((true, (GoalStateSnapshot?)(state! with { Goal = changed }), true)));
                    break;
                case "creation":
                    await fixture.Repository.TransactGoalCreationWithOutboxAsync(
                        new GoalCreationLoadScope([], [], []), (kernel, _) =>
                        {
                            creationCalled = true;
                            kernel.ReplaceGoalStateWithSnapshot(changed with
                                { Id = "bbbbbbbb000000000000000000000000" }, []);
                            return Task.FromResult((true, true,
                                (IReadOnlyList<OrchestratorStateOutboxMessage>)[]));
                        });
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(writer));
            }
        });

        Assert.Contains($"recorded format level {CurrentLevel + 1}", exception.Message);
        Assert.Contains($"this build's format level {CurrentLevel}", exception.Message);
        Assert.False(creationCalled);
        Assert.Equal(goalsBefore, GoalRows(fixture));
        Assert.Equal(historyBefore, HistoryRows(fixture));
        Assert.Equal(levelBefore, LevelRow(fixture));
        // A fresh writer can acquire immediately after refusal: disposal released the transaction.
        using var connection = fixture.Open();
        Execute(connection, "BEGIN IMMEDIATE");
        Execute(connection, "ROLLBACK");
    }

    [Theory]
    [InlineData("no-table")]
    [InlineData("no-row")]
    [InlineData("lower")]
    public async Task MissingOrLowerLevel_SaveRecordsCurrentLevel(string initial)
    {
        using var fixture = new GoalTaskHistoryTestFixture();
        var goal = GoalTaskHistoryTestFixture.Snapshot();
        fixture.InsertLegacy(goal);
        Assert.Empty(fixture.Rows(
            "SELECT name FROM sqlite_schema WHERE type = 'table' AND name = 'store_schema_versions'"));
        if (initial == "no-row")
        {
            using var connection = fixture.Open();
            StoreSchemaVersions.UpgradeToCurrent(connection, StoreSchemaRegistry.StateStoreFormat);
            Execute(connection, "DELETE FROM store_schema_versions");
        }
        else if (initial == "lower")
            SetLevel(fixture, CurrentLevel - 1);
        using (var connection = fixture.Open())
            Assert.Equal(initial == "lower" ? CurrentLevel - 1 : (int?)null,
                StoreSchemaVersions.Read(connection, StoreSchemaRegistry.StateStoreFormat.StoreName));

        goal = goal with { Objective = "Accepted save" };
        await fixture.Repository.SaveGoalSnapshotsAsync([goal]);

        using (var connection = fixture.Open())
            Assert.Equal(CurrentLevel,
                StoreSchemaVersions.Read(connection, StoreSchemaRegistry.StateStoreFormat.StoreName));
        var applied = Assert.Single(fixture.Rows(
            "SELECT applied_at FROM store_schema_versions WHERE store_name = 'orchestrator-state'"));
        Assert.NotEqual(AppliedBefore, applied);
        Assert.True(DateTimeOffset.TryParse(applied, CultureInfo.InvariantCulture,
            DateTimeStyles.None, out _));
        Assert.Equal(goal.Objective, (await fixture.Repository.LoadGoalAsync(new(goal.Id)))!.Objective);
        await fixture.AssertMirroredAsync(goal);
    }

    [Fact]
    public async Task EqualLevel_SavePreservesLevelAndAppliedTime()
    {
        using var fixture = new GoalTaskHistoryTestFixture();
        var goal = GoalTaskHistoryTestFixture.Snapshot();
        await fixture.Repository.SaveGoalSnapshotsAsync([goal]);
        SetLevel(fixture, CurrentLevel);
        var before = LevelRow(fixture);

        goal = goal with { Objective = "Changed at current level" };
        await fixture.Repository.SaveGoalSnapshotsAsync([goal]);

        Assert.Equal(before, LevelRow(fixture));
        Assert.Equal(goal.Objective, (await fixture.Repository.LoadGoalAsync(new(goal.Id)))!.Objective);
    }

    [Fact]
    public async Task NewerLevel_ReadOnlyLoadPreservesGoalAndHistory()
    {
        using var fixture = new GoalTaskHistoryTestFixture();
        var goal = GoalTaskHistoryTestFixture.Snapshot();
        await fixture.Repository.SaveGoalSnapshotsAsync([goal]);
        SetLevel(fixture, CurrentLevel + 1);
        var goalsBefore = GoalRows(fixture);
        var historyBefore = HistoryRows(fixture);
        var levelBefore = LevelRow(fixture);

        var repository = SqliteOrchestratorStateRepository.OpenReadOnly(fixture.DatabasePath);
        var loaded = await repository.LoadGoalAsync(new(goal.Id));

        Assert.NotNull(loaded);
        Assert.Equal(goal.Id, loaded.Id);
        Assert.Equal(goal.Objective, loaded.Objective);
        Assert.Equal(goal.Tasks.Count, loaded.Tasks.Count);
        foreach (var task in goal.Tasks)
        {
            var actual = Assert.Single(loaded.Tasks.Where(item => item.Id == task.Id));
            Assert.Equal(GoalTaskHistoryTestFixture.Json(task.VerificationHistory),
                GoalTaskHistoryTestFixture.Json(actual.VerificationHistory));
            Assert.Equal(GoalTaskHistoryTestFixture.Json(task.DispatchHistory),
                GoalTaskHistoryTestFixture.Json(actual.DispatchHistory));
        }
        Assert.Equal(goalsBefore, GoalRows(fixture));
        Assert.Equal(historyBefore, HistoryRows(fixture));
        Assert.Equal(levelBefore, LevelRow(fixture));
    }

    [Fact]
    public void NoGoalsTable_FenceLeavesSchemaEmpty()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        Execute(connection, "BEGIN IMMEDIATE");
        StateStoreFormatFence.Enforce(connection);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_schema";
        Assert.Equal(0L, command.ExecuteScalar());
        Execute(connection, "ROLLBACK");
    }

    [Fact]
    public void MissingLevel_RollbackAlsoRollsBackTableAndStamp()
    {
        using var fixture = new GoalTaskHistoryTestFixture();
        using var connection = fixture.Open();
        Execute(connection, "BEGIN IMMEDIATE");
        StateStoreFormatFence.Enforce(connection);
        Assert.Equal(CurrentLevel,
            StoreSchemaVersions.Read(connection, StoreSchemaRegistry.StateStoreFormat.StoreName));
        Execute(connection, "ROLLBACK");
        Assert.Empty(fixture.Rows(
            "SELECT name FROM sqlite_schema WHERE type = 'table' AND name = 'store_schema_versions'"));
    }

    [Fact]
    public void EqualLevel_FenceReadsOneLevelAndIssuesNoWrites()
    {
        using var fixture = new GoalTaskHistoryTestFixture();
        SetLevel(fixture, CurrentLevel);
        using var connection = fixture.Open();
        Execute(connection, "BEGIN IMMEDIATE");
        var statements = new List<string>();
        delegate_trace callback = (_, statement) => statements.Add(statement.utf8_to_string());
        raw.sqlite3_trace(connection.Handle, callback, null);
        try
        {
            StateStoreFormatFence.Enforce(connection);
        }
        finally
        {
            raw.sqlite3_trace(connection.Handle, (delegate_trace)null!, null);
            GC.KeepAlive(callback);
        }
        Assert.Single(statements.Where(sql => sql.StartsWith(
            "SELECT version FROM store_schema_versions WHERE store_name =", StringComparison.Ordinal)));
        Assert.All(statements, sql => Assert.StartsWith("SELECT", sql));
        Execute(connection, "ROLLBACK");
    }

    private static void SetLevel(GoalTaskHistoryTestFixture fixture, int level)
    {
        using var connection = fixture.Open();
        StoreSchemaVersions.UpgradeToCurrent(connection, StoreSchemaRegistry.StateStoreFormat);
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE store_schema_versions SET version = $level, applied_at = $at
            WHERE store_name = 'orchestrator-state'
            """;
        command.Parameters.AddWithValue("$level", level);
        command.Parameters.AddWithValue("$at", AppliedBefore);
        Assert.Equal(1, command.ExecuteNonQuery());
    }

    private static string[] GoalRows(GoalTaskHistoryTestFixture fixture) => RowBytes(fixture, "goals",
        "id", "status", "objective", "source_backlog_item_id", "updated_at", "snapshot_json", "version");

    private static string[] HistoryRows(GoalTaskHistoryTestFixture fixture) => RowBytes(fixture,
        "goal_task_history", "goal_id", "task_id", "kind", "sequence", "entry_json");

    // Hex preserves exact payload bytes; the storage type also distinguishes NULL from empty text.
    private static string[] RowBytes(GoalTaskHistoryTestFixture fixture, string table, params string[] columns) =>
        fixture.Rows($"SELECT {string.Join(" || '|' || ", columns.Select(column =>
            $"typeof({column}) || ':' || hex({column})"))} FROM {table} ORDER BY {string.Join(", ", columns)}");

    private static string[] LevelRow(GoalTaskHistoryTestFixture fixture) => RowBytes(fixture,
        "store_schema_versions", "store_name", "version", "applied_at");

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
