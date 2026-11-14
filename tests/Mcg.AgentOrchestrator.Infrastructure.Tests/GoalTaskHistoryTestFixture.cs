using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

internal sealed class GoalTaskHistoryTestFixture : IDisposable
{
    internal static readonly DateTimeOffset At = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);
    internal static JsonSerializerOptions Options => SqliteOrchestratorStateRepository.HistorySerializerOptions;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "goal-task-history", Guid.NewGuid().ToString("N"));
    internal string DatabasePath => Path.Combine(_root, "state.db");
    internal string DiagnosticsPath => Path.Combine(_root, "writes.jsonl");
    internal SqliteOrchestratorStateRepository Repository { get; }
    internal GoalTaskHistoryStore Store { get; } = new(Options);

    internal GoalTaskHistoryTestFixture()
    {
        Directory.CreateDirectory(_root);
        StateDbMigrations.EnsureUpToDate(DatabasePath);
        Repository = new(DatabasePath, null, new SqliteWriteTelemetryOptions
        {
            DiagnosticsPath = DiagnosticsPath,
            WarningHoldThreshold = TimeSpan.Zero,
            HoldDurationSource = _ => () => TimeSpan.FromMilliseconds(1),
            MirrorToConductEventStream = false
        });
    }

    internal static GoalSnapshot Snapshot(int entries = 10, int taskCount = 2)
    {
        const string goalId = "aaaaaaaa000000000000000000000000";
        var tasks = Enumerable.Range(1, taskCount).Select(number =>
        {
            var verification = Enumerable.Range(0, entries).Select(index => new TaskVerificationSnapshot(
                "verify", "fixture", index % 2, $"result {index} café", "", At.AddMinutes(index + 1),
                DispatchStartedAt: At.AddMinutes(index))).ToArray();
            var dispatch = Enumerable.Range(0, entries).Select(index => new TaskDispatchSnapshot(
                "fixture", "dispatch", "fixture", At.AddMinutes(index), ProviderName: "OpenAI")).ToArray();
            return new TaskSnapshot(number.ToString("x32"), $"Task {number}", AgentRole.Developer,
                WorkTaskStatus.Completed, null, null, verification.LastOrDefault(), verification,
                dispatch.LastOrDefault(), null, DispatchHistory: dispatch);
        }).ToArray();
        return new(goalId, "History fixture", GoalStatus.Completed, tasks,
            [new ProgressEventSnapshot(goalId, null, ProgressKind.GoalCreated, "Created", At.AddDays(-1))]);
    }

    internal static string Json<T>(T value) => JsonSerializer.Serialize(value, Options);
    internal SqliteConnection Open() => StateDbConnectionFactory.Open(DatabasePath, StateDbConnectionProfile.ReadWrite);

    internal string RawGoal(string id)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT snapshot_json FROM goals WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);
        return (string)command.ExecuteScalar()!;
    }

    internal void InsertLegacy(GoalSnapshot goal)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO goals (id, status, objective, updated_at, snapshot_json, version)
            VALUES ($id, $status, $objective, 'legacy', $json, 1)
            """;
        command.Parameters.AddWithValue("$id", goal.Id);
        command.Parameters.AddWithValue("$status", goal.Status.ToString());
        command.Parameters.AddWithValue("$objective", goal.Objective);
        command.Parameters.AddWithValue("$json", Json(goal));
        command.ExecuteNonQuery();
    }

    internal void Execute(string sql)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    internal string[] Rows(string sql)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var rows = new List<string>();
        while (reader.Read()) rows.Add(reader.GetString(0));
        return rows.ToArray();
    }

    internal string[] HistoryRows() => Rows("""
        SELECT goal_id || '|' || task_id || '|' || kind || '|' || sequence || '|' || entry_json
        FROM goal_task_history ORDER BY goal_id, task_id, kind, sequence
        """);

    internal async Task AssertMirroredAsync(GoalSnapshot goal)
    {
        using var connection = Open();
        var raw = RawGoal(goal.Id);
        Assert.Equal(Json(goal), raw);
        Assert.True(await Store.IsConsistentAsync(connection, goal.Id, raw));
        using var document = JsonDocument.Parse(raw);
        foreach (var task in document.RootElement.GetProperty("Tasks").EnumerateArray())
        {
            var history = await Store.ReadAsync(connection, goal.Id, task);
            foreach (var (property, result) in new[]
            {
                (GoalTaskHistoryStore.VerificationProperty, history.Verification),
                (GoalTaskHistoryStore.DispatchProperty, history.Dispatch)
            })
            {
                var inline = task.GetProperty(property);
                var expected = inline.ValueKind == JsonValueKind.Null ? [] : inline.EnumerateArray().Select(entry => entry.GetRawText()).ToArray();
                Assert.Equal(expected, result.Entries.Select(entry => entry.GetRawText()));
                Assert.Equal(expected.Length > 0, result.FromTable);
            }
        }
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
