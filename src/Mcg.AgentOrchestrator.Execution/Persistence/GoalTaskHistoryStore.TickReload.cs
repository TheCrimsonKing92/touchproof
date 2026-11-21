using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Microsoft.Data.Sqlite;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed partial class GoalTaskHistoryStore
{
    private sealed record RowSummary(long Count, long LastSequence, string LatestJson);

    internal async Task<GoalSnapshot> ProjectTickTasksAsync(SqliteConnection connection, GoalSnapshot snapshot,
        DocumentMarkers markers, Func<string, TaskHistoryRows> readHistory, CancellationToken token)
    {
        if (snapshot.Tasks is null) return snapshot;
        var countsByTask = new Dictionary<string, HistoryMarker>(StringComparer.Ordinal);
        foreach (var (taskId, marker) in markers.ForTasks(snapshot.Tasks))
            if (ReadMarker(marker, snapshot.Id, taskId) is { } counts)
                countsByTask[taskId] = counts;

        Dictionary<(string TaskId, string Kind), RowSummary> summaries = countsByTask.Count == 0 ? [] :
            await ReadSummariesAsync(connection, snapshot.Id, countsByTask.Keys.First(), token);
        var tasks = new List<TaskSnapshot>(snapshot.Tasks.Count);
        foreach (var task in snapshot.Tasks)
        {
            TaskVerificationSnapshot? latestVerification = task.LastVerification;
            TaskDispatchSnapshot? latestDispatch = task.LastDispatch;
            var verificationCount = task.VerificationHistory?.Count ?? 0;
            var dispatchCount = task.DispatchHistory?.Count ?? 0;
            int? restoredVerificationCount = null;
            if (countsByTask.TryGetValue(task.Id, out var counts))
            {
                var verification = ValidateSummary(summaries, snapshot.Id, task.Id, "verification", counts.Verification);
                var dispatch = ValidateSummary(summaries, snapshot.Id, task.Id, "dispatch", counts.Dispatch);
                verificationCount = counts.Verification ?? 0;
                dispatchCount = counts.Dispatch ?? 0;
                if (counts.LatestVerificationInTable)
                    latestVerification = ReadLatest<TaskVerificationSnapshot>(verification!, snapshot.Id, task.Id, "verification");
                if (counts.LatestDispatchInTable)
                    latestDispatch = ReadLatest<TaskDispatchSnapshot>(dispatch!, snapshot.Id, task.Id, "dispatch");
                // A separate inline latest may represent a round absent from the stored list.
                if (verificationCount <= TaskSpec.VerificationHistoryLimit &&
                    (counts.LatestVerificationInTable || latestVerification is null))
                    restoredVerificationCount = verificationCount;
            }
            if (verificationCount == 0)
                restoredVerificationCount = latestVerification is null ? 0 : 1;

            var taskId = task.Id;
            tasks.Add(task with
            {
                VerificationHistory = [],
                DispatchHistory = [],
                LastVerification = latestVerification,
                LastDispatch = latestDispatch,
                DeferredHistory = new(verificationCount, dispatchCount, restoredVerificationCount,
                    () => readHistory(taskId))
            });
        }
        return await _buildHistory.HydrateAsync(connection, snapshot with { Tasks = tasks }, token);
    }

    private static async Task<Dictionary<(string TaskId, string Kind), RowSummary>> ReadSummariesAsync(
        SqliteConnection connection, string goalId, string requiredTaskId, CancellationToken token)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            WITH bounds AS (
                SELECT task_id, kind, COUNT(*) AS count, MAX(sequence) AS last_sequence
                FROM goal_task_history WHERE goal_id = $goal GROUP BY task_id, kind
            )
            SELECT b.task_id, b.kind, b.count, b.last_sequence, h.entry_json
            FROM bounds b JOIN goal_task_history h
              ON h.goal_id = $goal AND h.task_id = b.task_id AND h.kind = b.kind AND h.sequence = b.last_sequence
            """;
        command.Parameters.AddWithValue("$goal", goalId);
        var summaries = new Dictionary<(string, string), RowSummary>();
        try
        {
            using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
                summaries.Add((reader.GetString(0), reader.GetString(1)),
                    new(reader.GetInt64(2), reader.GetInt64(3), reader.GetString(4)));
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 1 &&
            ex.Message.Contains("no such table: goal_task_history", StringComparison.OrdinalIgnoreCase))
        {
            throw HistoryError(goalId, requiredTaskId, "both", "missing goal_task_history table", ex);
        }
        return summaries;
    }

    private static RowSummary? ValidateSummary(Dictionary<(string TaskId, string Kind), RowSummary> summaries,
        string goalId, string taskId, string kind, int? expected)
    {
        var result = summaries.GetValueOrDefault((taskId, kind));
        var actual = result?.Count ?? 0;
        if (actual != (expected ?? 0))
            throw HistoryError(goalId, taskId, kind, $"expected {expected ?? 0} rows, found {actual}");
        if (result is not null && result.LastSequence != actual - 1)
            throw HistoryError(goalId, taskId, kind, "sequence gap");
        return result;
    }

    private T ReadLatest<T>(RowSummary row, string goalId, string taskId, string kind) where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(row.LatestJson, options)
                ?? throw new JsonException("History entry must be an object.");
        }
        catch (JsonException ex)
        {
            throw HistoryError(goalId, taskId, kind, $"unparseable entry at {row.LastSequence}", ex);
        }
    }
}
