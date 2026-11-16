using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Microsoft.Data.Sqlite;

// Parallel-safe: compatibility decoding reads only a private fixture database.
public sealed class GoalTaskHistoryMarkerContractTests
{
    [Fact]
    public async Task NewWriterIsReadableByLatestAwareMarkerContractAndStep3aHydration()
    {
        using var fixture = new GoalTaskHistoryTestFixture();
        var goal = GoalTaskHistoryTestFixture.Snapshot(taskCount: 3);
        goal = goal with { Tasks = [goal.Tasks[0], goal.Tasks[1] with
            { VerificationHistory = [], DispatchHistory = null }, goal.Tasks[2] with
            { VerificationHistory = null, DispatchHistory = [] }] };
        await fixture.Repository.SaveGoalSnapshotsAsync([goal]);
        var raw = fixture.RawGoal(goal.Id);
        using var document = JsonDocument.Parse(raw);
        using var connection = fixture.Open();
        var snapshot = JsonSerializer.Deserialize<GoalSnapshot>(raw, GoalTaskHistoryTestFixture.Options)!;
        var tasks = new List<TaskSnapshot>();
        foreach (var task in document.RootElement.GetProperty("Tasks").EnumerateArray())
        {
            Assert.False(task.TryGetProperty("VerificationHistory", out _));
            Assert.False(task.TryGetProperty("DispatchHistory", out _));
            var id = task.GetProperty("Id").GetString()!;
            var marker = task.GetProperty("HistoryInTable");
            var source = snapshot.Tasks.Single(value => value.Id == id);
            var verification = ReadEntries<TaskVerificationSnapshot>(connection, goal.Id, id, "verification", marker.GetProperty("Verification"));
            var dispatch = ReadEntries<TaskDispatchSnapshot>(connection, goal.Id, id, "dispatch", marker.GetProperty("Dispatch"));
            tasks.Add(source with
            {
                VerificationHistory = verification,
                DispatchHistory = dispatch,
                LastVerification = marker.TryGetProperty("LatestVerificationInTable", out var v) && v.GetBoolean()
                    ? verification![^1] : source.LastVerification,
                LastDispatch = marker.TryGetProperty("LatestDispatchInTable", out var d) && d.GetBoolean()
                    ? dispatch![^1] : source.LastDispatch
            });
        }
        // This decoder covers task-history markers; the full hydration below also restores TimelineInTable.
        Assert.Equal(GoalTaskHistoryTestFixture.Json(goal.Tasks), GoalTaskHistoryTestFixture.Json(tasks));
        // HydrateAsync includes the step 3a latest reader, without writer-specific options or decoding.
        Assert.Equal(GoalTaskHistoryTestFixture.Json(goal), GoalTaskHistoryTestFixture.Json(
            await fixture.Store.HydrateAsync(connection, snapshot, raw)));
    }

    private static IReadOnlyList<T>? ReadEntries<T>(SqliteConnection connection, string goal, string task,
        string kind, JsonElement count)
    {
        Assert.True(count.ValueKind is JsonValueKind.Null or JsonValueKind.Number);
        int? expected = count.ValueKind == JsonValueKind.Null ? null : count.GetInt32();
        Assert.True(expected is null or >= 0);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT sequence, entry_json FROM goal_task_history WHERE goal_id = $goal AND task_id = $task AND kind = $kind ORDER BY sequence";
        command.Parameters.AddWithValue("$goal", goal);
        command.Parameters.AddWithValue("$task", task);
        command.Parameters.AddWithValue("$kind", kind);
        using var reader = command.ExecuteReader();
        var entries = new List<T>();
        while (reader.Read())
        {
            Assert.Equal(entries.Count, reader.GetInt32(0));
            entries.Add(JsonSerializer.Deserialize<T>(reader.GetString(1), GoalTaskHistoryTestFixture.Options)!);
        }
        Assert.Equal(expected ?? 0, entries.Count);
        return expected is null ? null : entries;
    }
}
