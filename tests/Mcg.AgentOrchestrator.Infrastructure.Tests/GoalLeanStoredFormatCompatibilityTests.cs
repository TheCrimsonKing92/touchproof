using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

// Parallel-safe: the fixture owns its database and the read oracle uses a local connection.
public sealed class GoalLeanStoredFormatCompatibilityTests
{
    [Fact]
    public async Task PreChangeDocumentAndRowReaderReadsTheCompleteLeanCheckpoint()
    {
        using var support = new GoalTimelineTestSupport();
        var seed = GoalTimelineTestSupport.Snapshot();
        await support.SeedAsync(seed);
        var options = new JsonSerializerOptions();
        options.Converters.Add(new JsonStringEnumConverter());
        var original = ReadPreChangeGoal(support.Fixture, seed.Id, options);
        Assert.Equal(JsonSerializer.Serialize(seed, options), JsonSerializer.Serialize(original, options));

        var id = new GoalId(seed.Id);
        var memory = new GoalReloadMemory();
        var reload = await support.Repository.LoadGoalsReusingUnchangedAsync([id], memory);
        var baseline = Assert.Single(OrchestratorSnapshot.Capture(reload.Kernel, includeDeferredRows: false).Goals);
        Assert.Empty(baseline.Timeline);
        Assert.All(baseline.Tasks, task =>
        {
            Assert.Empty(task.VerificationHistory!);
            Assert.Empty(task.DispatchHistory!);
        });

        var appended = new ProgressEventSnapshot(seed.Id, null, ProgressKind.TaskNote, "Lean checkpoint",
            GoalTimelineTestSupport.At.AddHours(1));
        reload.Kernel.GetGoal(id).Append(new(id, null, appended.Kind, appended.Message, appended.OccurredAt));
        var current = Assert.Single(OrchestratorSnapshot.Capture(reload.Kernel, includeDeferredRows: false).Goals);
        var save = Assert.Single(await support.Repository.SaveGoalSnapshotsWithMergeAsync([new(baseline, current)]));
        Assert.Equal(GoalSnapshotSaveDisposition.Saved, save.Disposition);

        using var stored = JsonDocument.Parse(support.Fixture.RawGoal(seed.Id));
        Assert.True(stored.RootElement.TryGetProperty("TimelineInTable", out _));
        Assert.All(stored.RootElement.GetProperty("Tasks").EnumerateArray(),
            task => Assert.True(task.TryGetProperty("HistoryInTable", out _)));

        var expected = original with { Timeline = [.. original.Timeline, appended] };
        var actual = ReadPreChangeGoal(support.Fixture, seed.Id, options);
        Assert.Equal(JsonSerializer.Serialize(expected, options), JsonSerializer.Serialize(actual, options));
    }

    // The inline model and split row layout come from the existing history and Timeline
    // fixtures. This reader uses their persisted names and ordering directly.
    private static GoalSnapshot ReadPreChangeGoal(GoalTaskHistoryTestFixture fixture, string id, JsonSerializerOptions options)
    {
        var goal = JsonNode.Parse(fixture.RawGoal(id))!.AsObject();
        using var connection = fixture.Open();
        if (goal.TryGetPropertyValue("TimelineInTable", out var timelineCount))
        {
            goal["Timeline"] = ReadRows(connection, id, timelineCount!.GetValue<int>());
            goal.Remove("TimelineInTable");
        }

        foreach (var entry in goal["Tasks"]!.AsArray())
        {
            var task = entry!.AsObject();
            if (task["HistoryInTable"] is not JsonObject marker) continue;
            var taskId = task["Id"]!.GetValue<string>();
            foreach (var (property, kind) in new[] { ("Verification", "verification"), ("Dispatch", "dispatch") })
            {
                Assert.True(marker.TryGetPropertyValue(property, out var count));
                var history = ReadRows(connection, id, count?.GetValue<int>() ?? 0, taskId, kind);
                task[property + "History"] = count is null ? null : history;
                if (marker["Latest" + property + "InTable"]?.GetValue<bool>() == true)
                    task["Last" + property] = history[history.Count - 1]!.DeepClone();
            }
            task.Remove("HistoryInTable");
        }

        return goal.Deserialize<GoalSnapshot>(options)!;
    }

    private static JsonArray ReadRows(SqliteConnection connection, string goalId, int count,
        string? taskId = null, string? kind = null)
    {
        using var command = connection.CreateCommand();
        command.Parameters.AddWithValue("$goal", goalId);
        if (taskId is null)
            command.CommandText = "SELECT sequence, entry_json FROM goal_timeline WHERE goal_id = $goal ORDER BY sequence";
        else
        {
            command.CommandText = """
                SELECT sequence, entry_json FROM goal_task_history
                WHERE goal_id = $goal AND task_id = $task AND kind = $kind ORDER BY sequence
                """;
            command.Parameters.AddWithValue("$task", taskId);
            command.Parameters.AddWithValue("$kind", kind!);
        }

        var rows = new JsonArray();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            Assert.Equal((long)rows.Count, reader.GetInt64(0));
            rows.Add(JsonNode.Parse(reader.GetString(1)));
        }
        Assert.Equal(count, rows.Count);
        return rows;
    }
}
