using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Microsoft.Data.Sqlite;

namespace Mcg.AgentOrchestrator.Infrastructure;

/// <summary>
/// Stores the complete ordered invocation list for each marked task. New positions append rows.
/// Updates to an invocation or its list position replace the payload at that sequence.
/// <para>
/// TaskSpec and AgentOrchestratorKernel.Recording restore, export and reconcile the hydrated list.
/// WorkerBuildInfrastructureEvidence, WorkerBuildCapacityEvidence and DeveloperBuildReportEvidence
/// evaluate that list. WorkerBuildInvocationOperations, WorkerBuildRequestHost,
/// WorkerBuildEvidenceRecoveryHold, ExitedDispatchReconciliationExecutor and StatusAgentReadView
/// use hydrated task state. WorkerBuildInvocation and WorkerBuildStructuralCheck validate entries
/// from that state during reconciliation. WorkerBuildStructuralPreflight receives hydrated entries
/// from WorkerBuildRequestHost during replay. BackgroundDispatchRunner and CompletedProcessOutcomeBuilder
/// use hydrated state for reconciliation and evidence consumption. WorkerBuildApplicabilityExplanation
/// filters the hydrated list, and WorkerBuildApplicabilityFormatter and OrchestratorBuildEvidenceCheck
/// report comparisons derived from it.
/// </para>
/// <para>
/// GoalTaskHistoryStore composes hydration for repository loads, prefix lookups, versioned reads,
/// backlog reads and archive reads. SqliteOrchestratorStateRepository merges full hydrated lists.
/// TerminalOpenWorkIndex uses the inline open entries for the scan, index triggers and backfill.
/// Repository terminal candidate reads, GoalSnapshotArchive and TerminalOpenWorkCandidates use
/// the resulting flag. Unmarked legacy tasks use their full inline list until saved.
/// </para>
/// <para>
/// Adopt with older writers stopped. A pre-adoption writer can discard the marker and make
/// the inline open entries appear to be the full legacy list.
/// </para>
/// </summary>
internal sealed class GoalTaskBuildHistoryStore(JsonSerializerOptions options)
{
    internal sealed record PreparedWrite(GoalSnapshot ProjectedGoal, string RowsJson);

    private sealed record WriteRow(string TaskId, int Sequence, string EntryJson);

    internal PreparedWrite PrepareWrite(GoalSnapshot goal)
    {
        var rows = new List<WriteRow>();
        var tasks = new List<TaskSnapshot>(goal.Tasks.Count);
        foreach (var task in goal.Tasks)
        {
            if (task.WorkerBuildInvocationsInTable is not null)
                throw new InvalidOperationException(
                    $"Refusing to save build history before hydrating goal '{goal.Id}' task '{task.Id}'.");

            if (task.WorkerBuildInvocations is not { } invocations)
            {
                tasks.Add(task);
                continue;
            }

            for (var sequence = 0; sequence < invocations.Count; sequence++)
                rows.Add(new(task.Id, sequence, JsonSerializer.Serialize(invocations[sequence], options)));

            tasks.Add(task with
            {
                WorkerBuildInvocations = invocations.Where(invocation => !invocation.IsTerminal || !invocation.CustodyReleased).ToArray(),
                WorkerBuildInvocationsInTable = invocations.Count
            });
        }

        return new(goal with { Tasks = tasks }, JsonSerializer.Serialize(rows, options));
    }

    [StoreSchemaSource("orchestrator-state", "src/Mcg.AgentOrchestrator.Execution/Persistence/GoalTaskBuildHistoryStore.cs")]
    private static void CreateSchema(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS goal_task_build_history (
                goal_id TEXT NOT NULL,
                task_id TEXT NOT NULL,
                kind TEXT NOT NULL CHECK (kind = 'build'),
                sequence INTEGER NOT NULL CHECK (sequence >= 0),
                entry_json TEXT NOT NULL,
                PRIMARY KEY (goal_id, task_id, kind, sequence)
            )
            """;
        command.ExecuteNonQuery();
    }

    // Schema, rows and document share the caller's goal write transaction.
    internal async Task<bool> WriteAsync(SqliteConnection connection, PreparedWrite prepared, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        CreateSchema(connection);
        using var command = connection.CreateCommand();
        command.Parameters.AddWithValue("$goal", prepared.ProjectedGoal.Id);
        command.Parameters.AddWithValue("$rows", prepared.RowsJson);
        command.CommandText = """
            INSERT INTO goal_task_build_history (goal_id, task_id, kind, sequence, entry_json)
            SELECT $goal, json_extract(value, '$.TaskId'), 'build',
                json_extract(value, '$.Sequence'), json_extract(value, '$.EntryJson')
            FROM json_each($rows) WHERE true
            ON CONFLICT(goal_id, task_id, kind, sequence) DO UPDATE SET entry_json = excluded.entry_json
            WHERE entry_json != excluded.entry_json
            """;
        var changed = await command.ExecuteNonQueryAsync(token);
        command.CommandText = """
            DELETE FROM goal_task_build_history WHERE goal_id = $goal
                AND (task_id, sequence) NOT IN (
                    SELECT json_extract(value, '$.TaskId'), json_extract(value, '$.Sequence')
                    FROM json_each($rows)
                )
            """;
        changed += await command.ExecuteNonQueryAsync(token);
        return changed > 0;
    }

    internal async Task<GoalSnapshot> HydrateAsync(SqliteConnection connection, GoalSnapshot goal, CancellationToken token)
    {
        var marked = goal.Tasks.Where(task => task.WorkerBuildInvocationsInTable.HasValue).ToArray();
        if (marked.Length == 0) return goal;

        var histories = marked.ToDictionary(task => task.Id, _ => new List<WorkerBuildInvocation>());
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT task_id, sequence, entry_json FROM goal_task_build_history
            WHERE goal_id = $goal AND kind = 'build' AND task_id IN (SELECT value FROM json_each($tasks))
            ORDER BY task_id, sequence
            """;
        command.Parameters.AddWithValue("$goal", goal.Id);
        command.Parameters.AddWithValue("$tasks", JsonSerializer.Serialize(histories.Keys, options));
        try
        {
            using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                var taskId = reader.GetString(0);
                var history = histories[taskId];
                var sequence = reader.GetInt64(1);
                if (sequence != history.Count)
                    throw HistoryError(goal.Id, taskId, $"expected sequence {history.Count}, found {sequence}");

                history.Add(ReadInvocation(reader.GetString(2), goal.Id, taskId, sequence));
            }
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 1 &&
            ex.Message.Contains("no such table: goal_task_build_history", StringComparison.OrdinalIgnoreCase))
        {
            throw HistoryError(goal.Id, marked[0].Id, "missing goal_task_build_history table", ex);
        }

        foreach (var task in marked)
        {
            var count = histories[task.Id].Count;
            if (count != task.WorkerBuildInvocationsInTable)
                throw HistoryError(goal.Id, task.Id, $"expected {task.WorkerBuildInvocationsInTable} rows, found {count}");
        }

        return goal with
        {
            Tasks = goal.Tasks.Select(task => task.WorkerBuildInvocationsInTable is null ? task : task with
            {
                WorkerBuildInvocations = histories[task.Id].ToArray(),
                WorkerBuildInvocationsInTable = null
            }).ToArray()
        };
    }

    private WorkerBuildInvocation ReadInvocation(string json, string goalId, string taskId, long sequence)
    {
        try
        {
            var invocation = JsonSerializer.Deserialize<WorkerBuildInvocation>(json, options);
            return invocation is not null && !string.IsNullOrWhiteSpace(invocation.Id)
                ? invocation : throw new JsonException("Missing invocation identity.");
        }
        catch (JsonException ex)
        {
            throw HistoryError(goalId, taskId, $"invalid invocation at sequence {sequence}", ex);
        }
    }

    private static InvalidDataException HistoryError(string goalId, string taskId, string defect, Exception? inner = null) =>
        new($"Goal '{goalId}' task '{taskId}' build history: {defect}.", inner);
}
