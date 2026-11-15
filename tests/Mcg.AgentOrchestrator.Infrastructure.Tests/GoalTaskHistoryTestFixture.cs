using System.Text.Json;
using System.Text.Json.Nodes;
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
    internal long ReportedBytes()
    {
        using var receipt = JsonDocument.Parse(File.ReadAllLines(DiagnosticsPath)
            .Last(line => line.Contains("sqlite-state-write-hold", StringComparison.Ordinal)));
        return receipt.RootElement.GetProperty("serializedBytes").GetInt64();
    }

    internal void AssertLatestRepresentation(GoalSnapshot goal, bool verification = true, bool dispatch = true)
    {
        using var document = JsonDocument.Parse(RawGoal(goal.Id));
        foreach (var task in document.RootElement.GetProperty("Tasks").EnumerateArray())
        {
            var source = goal.Tasks.Single(t => t.Id == task.GetProperty("Id").GetString());
            var marker = task.GetProperty("HistoryInTable");
            foreach (var (property, flag, marked, expected) in new[]
            {
                ("LastVerification", "LatestVerificationInTable", verification, Json(source.LastVerification)),
                ("LastDispatch", "LatestDispatchInTable", dispatch, Json(source.LastDispatch))
            })
            {
                if (marked)
                {
                    Assert.False(task.TryGetProperty(property, out _));
                    Assert.Equal(JsonValueKind.True, marker.GetProperty(flag).ValueKind);
                }
                else
                {
                    // JsonNode writes may escape strings differently while preserving every value.
                    Assert.True(JsonNode.DeepEquals(JsonNode.Parse(expected),
                        JsonNode.Parse(task.GetProperty(property).GetRawText())),
                        $"Inline {property} must preserve the complete latest record.");
                    Assert.False(marker.TryGetProperty(flag, out _));
                }
            }
        }
    }

    // Recreate the step 3a document without changing the goal or history rows.
    internal void InlineStoredLatest(GoalSnapshot goal)
    {
        var document = System.Text.Json.Nodes.JsonNode.Parse(RawGoal(goal.Id))!;
        foreach (var task in document["Tasks"]!.AsArray())
        {
            var source = goal.Tasks.Single(t => t.Id == task!["Id"]!.GetValue<string>());
            task!["HistoryInTable"]!.AsObject().Remove("LatestVerificationInTable");
            task["HistoryInTable"]!.AsObject().Remove("LatestDispatchInTable");
            task["LastVerification"] = JsonSerializer.SerializeToNode(source.LastVerification, Options);
            task["LastDispatch"] = JsonSerializer.SerializeToNode(source.LastDispatch, Options);
        }
        ReplaceDocument(goal.Id, document.ToJsonString(Options));
    }
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

    internal void MarkStoredHistory(string goalId, bool keepInline = false)
    {
        var document = System.Text.Json.Nodes.JsonNode.Parse(RawGoal(goalId))!;
        foreach (var task in document["Tasks"]!.AsArray())
        {
            task!["HistoryInTable"] ??= new System.Text.Json.Nodes.JsonObject
            {
                ["Verification"] = task["VerificationHistory"]?.AsArray().Count,
                ["Dispatch"] = task["DispatchHistory"]?.AsArray().Count
            };
            if (!keepInline)
            {
                task.AsObject().Remove("VerificationHistory");
                task.AsObject().Remove("DispatchHistory");
            }
        }
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE goals SET snapshot_json = $json, version = version + 1 WHERE id = $id";
        command.Parameters.AddWithValue("$json", document.ToJsonString(Options));
        command.Parameters.AddWithValue("$id", goalId);
        Assert.Equal(1, command.ExecuteNonQuery());
    }

    internal async Task AssertMirroredAsync(GoalSnapshot goal)
    {
        using var connection = Open();
        var raw = RawGoal(goal.Id);
        Assert.True(await Store.IsConsistentAsync(connection, goal.Id, raw));
        using var document = JsonDocument.Parse(raw);
        foreach (var task in document.RootElement.GetProperty("Tasks").EnumerateArray())
        {
            var source = goal.Tasks.Single(source => source.Id == task.GetProperty("Id").GetString());
            Assert.False(task.TryGetProperty("VerificationHistory", out _));
            Assert.False(task.TryGetProperty("DispatchHistory", out _));
            var marker = task.GetProperty("HistoryInTable");
            Assert.Equal(Json(source.VerificationHistory?.Count), marker.GetProperty("Verification").GetRawText());
            Assert.Equal(Json(source.DispatchHistory?.Count), marker.GetProperty("Dispatch").GetRawText());
            var history = await Store.ReadAsync(connection, goal.Id, task);
            foreach (var (expectedList, result) in new[]
            {
                (JsonSerializer.SerializeToElement(source.VerificationHistory, Options), history.Verification),
                (JsonSerializer.SerializeToElement(source.DispatchHistory, Options), history.Dispatch)
            })
            {
                var expected = expectedList.ValueKind == JsonValueKind.Null ? [] : expectedList.EnumerateArray().Select(entry => entry.GetRawText()).ToArray();
                Assert.Equal(expected, result.Entries.Select(entry => entry.GetRawText()));
                Assert.True(result.FromTable);
                Assert.Equal(expectedList.ValueKind == JsonValueKind.Null, result.IsNull);
            }
        }
    }

    internal void ReplaceDocument(string id, string json)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE goals SET snapshot_json = $json WHERE id = $id";
        command.Parameters.AddWithValue("$json", json);
        command.Parameters.AddWithValue("$id", id);
        Assert.Equal(1, command.ExecuteNonQuery());
    }

    internal void MarkStoredLatest(string goalId, bool? verification = true, bool? dispatch = true,
        bool keepInline = false)
    {
        var document = System.Text.Json.Nodes.JsonNode.Parse(RawGoal(goalId))!;
        var index = 0;
        foreach (var task in document["Tasks"]!.AsArray())
        {
            var marker = task!["HistoryInTable"]!.AsObject();
            if (verification is { } v) marker["LatestVerificationInTable"] = v;
            if (dispatch is { } d) marker["LatestDispatchInTable"] = d;
            foreach (var (property, marked) in new[] { ("LastVerification", verification), ("LastDispatch", dispatch) })
            {
                if (marked != true || keepInline) continue;
                if (index % 2 == 0) task.AsObject().Remove(property);
                else task[property] = null;
            }
            index++;
        }
        ReplaceDocument(goalId, document.ToJsonString(Options));
    }

    internal static GoalSnapshot LatestSnapshot()
    {
        var seed = Snapshot(entries: 3);
        // Different per-task payloads detect accidental cross-task selection.
        seed = seed with { Tasks = seed.Tasks.Select(task =>
        {
            var verification = task.VerificationHistory!.Select(entry => entry with
                { StandardOutput = task.Id + entry.StandardOutput }).ToArray();
            var dispatch = task.DispatchHistory!.Select(entry => entry with
                { Command = task.Id + entry.Command, ResultCommit = task.Id + entry.DispatchedAt.Minute }).ToArray();
            return task with { VerificationHistory = verification, LastVerification = verification[^1],
                DispatchHistory = dispatch, LastDispatch = dispatch[^1] };
        }).ToArray() };
        return AgentOrchestratorKernel.FromSnapshot(new([seed], [])).ExportGoalSnapshot(new(seed.Id));
    }

    internal async Task<IReadOnlyList<GoalSnapshot>> ReadLatestPathsAsync(GoalSnapshot goal, bool archived = false)
    {
        var id = new GoalId(goal.Id);
        var loaded = new List<GoalSnapshot>
        {
            (await Repository.LoadGoalsAsync([id])).ExportGoalSnapshot(id),
            (await Repository.LoadStatusGoalsAsync([id])).ExportGoalSnapshot(id),
            (await Repository.LoadGoalAsync(id))!,
            Assert.Single(await Repository.FindGoalSnapshotsByIdPrefixAsync(goal.Id[..8], 1)),
            (await Repository.TransactGoalAsync(id, (snapshot, _) => Task.FromResult((false, snapshot, snapshot))))!,
            (await Repository.TransactGoalStateAsync(id, (state, _) => Task.FromResult((false, state, state!.Goal))))!
        };
        if (!archived)
        {
            loaded.Add((await Repository.LoadAsync()).ExportGoalSnapshot(id));
            loaded.Add((await Repository.LoadBoardSnapshotAsync()).ExportGoalSnapshot(id));
            foreach (var task in goal.Tasks)
            {
                var match = Assert.Single(await Repository.FindTaskSnapshotsByIdPrefixAsync(task.Id, 1));
                loaded.Add(match.Goal with { Tasks = [match.Task] });
            }
        }
        Assert.Single(await Repository.ListCompletedGoalSimilarityDocumentsAsync());
        return loaded;
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
