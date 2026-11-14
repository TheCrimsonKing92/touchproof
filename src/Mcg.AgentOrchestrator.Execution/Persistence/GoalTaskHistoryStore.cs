using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Microsoft.Data.Sqlite;

namespace Mcg.AgentOrchestrator.Infrastructure;

/// <summary>A transactional mirror. Inline history remains authoritative, including after an older writer saves.</summary>
internal sealed class GoalTaskHistoryStore(JsonSerializerOptions options)
{
    internal const string VerificationProperty = "VerificationHistory";
    internal const string DispatchProperty = "DispatchHistory";

    [StoreSchemaSource("orchestrator-state", "src/Mcg.AgentOrchestrator.Execution/Persistence/GoalTaskHistoryStore.cs")]
    internal static void CreateSchema(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS goal_task_history (
                goal_id TEXT NOT NULL,
                task_id TEXT NOT NULL,
                kind TEXT NOT NULL CHECK (kind IN ('verification', 'dispatch')),
                sequence INTEGER NOT NULL CHECK (sequence >= 0),
                entry_json TEXT NOT NULL,
                PRIMARY KEY (goal_id, task_id, kind, sequence)
            )
            """;
        command.ExecuteNonQuery();
    }

    // All callers already hold the goal write transaction. Never begin or commit a separate transaction here.
    internal async Task WriteAsync(SqliteConnection connection, string goalId, string goalJson, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        CreateSchema(connection); // Existing stores do not rerun migration 1.
        using var document = JsonDocument.Parse(goalJson);
        var rows = new List<HistoryWriteRow>();
        foreach (var task in document.RootElement.GetProperty("Tasks").EnumerateArray())
        {
            var taskId = task.GetProperty("Id").GetString()!;
            foreach (var (property, kind) in Kinds)
            {
                var entries = Inline(task, property);
                for (var index = 0; index < entries.Length; index++)
                    rows.Add(new(taskId, kind, index, entries[index].GetRawText()));
            }
        }

        // Keep entry JSON as a string in the batch so SQLite preserves its exact serialized bytes.
        using var command = connection.CreateCommand();
        command.Parameters.AddWithValue("$goal", goalId);
        command.Parameters.AddWithValue("$rows", JsonSerializer.Serialize(rows, options));
        command.CommandText = """
            INSERT INTO goal_task_history (goal_id, task_id, kind, sequence, entry_json)
            SELECT $goal, json_extract(value, '$.TaskId'), json_extract(value, '$.Kind'),
                json_extract(value, '$.Sequence'), json_extract(value, '$.EntryJson')
            FROM json_each($rows) WHERE true
            ON CONFLICT(goal_id, task_id, kind, sequence) DO UPDATE SET entry_json = excluded.entry_json
            WHERE entry_json != excluded.entry_json
            """;
        await command.ExecuteNonQueryAsync(token);
        // Cap trims, list replacements and removed tasks all remove keys from the same goal-wide set.
        command.CommandText = """
            DELETE FROM goal_task_history WHERE goal_id = $goal
                AND (task_id, kind, sequence) NOT IN (
                    SELECT json_extract(value, '$.TaskId'), json_extract(value, '$.Kind'),
                        json_extract(value, '$.Sequence') FROM json_each($rows)
                )
            """;
        await command.ExecuteNonQueryAsync(token);
    }

    private sealed record HistoryWriteRow(string TaskId, string Kind, int Sequence, string EntryJson);

    private static readonly (string Property, string Kind)[] Kinds =
        [(VerificationProperty, "verification"), (DispatchProperty, "dispatch")];

    internal sealed record HistoryEntries(JsonElement[] Entries, bool FromTable);
    internal sealed record TaskHistory(HistoryEntries Verification, HistoryEntries Dispatch);

    internal Task<TaskHistory> ReadAsync(SqliteConnection? connection, string goalId, JsonElement task,
        CancellationToken token = default) => ReadAsync(connection, goalId, task.GetProperty("Id").GetString()!,
            task.TryGetProperty(VerificationProperty, out var verification) ? verification : default,
            task.TryGetProperty(DispatchProperty, out var dispatch) ? dispatch : default, token);

    internal async Task<TaskHistory> ReadAsync(SqliteConnection? connection, string goalId, string taskId,
        JsonElement verification, JsonElement dispatch, CancellationToken token = default)
    {
        var rows = connection is null ? null : await ReadRowsAsync(connection, goalId, taskId, token);
        return new(
            SelectHistory(rows?.GetValueOrDefault((taskId, "verification")), Elements(verification)),
            SelectHistory(rows?.GetValueOrDefault((taskId, "dispatch")), Elements(dispatch)));
    }

    private sealed record StoredEntry(long Sequence, string Json);

    private static async Task<Dictionary<(string TaskId, string Kind), List<StoredEntry>>> ReadRowsAsync(
        SqliteConnection connection, string goalId, string? taskId, CancellationToken token)
    {
        var rows = new Dictionary<(string TaskId, string Kind), List<StoredEntry>>();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT task_id, kind, sequence, entry_json FROM goal_task_history
            WHERE goal_id = $goal
            """ + (taskId is null ? "" : " AND task_id = $task") + " ORDER BY task_id, kind, sequence";
        command.Parameters.AddWithValue("$goal", goalId);
        if (taskId is not null) command.Parameters.AddWithValue("$task", taskId);
        try
        {
            using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                var key = (reader.GetString(0), reader.GetString(1));
                if (!rows.TryGetValue(key, out var entries)) rows.Add(key, entries = []);
                entries.Add(new(reader.GetInt64(2), reader.GetString(3)));
            }
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 1 &&
            ex.Message.Contains("no such table: goal_task_history", StringComparison.OrdinalIgnoreCase))
        {
            // Stores written before the mirror was introduced have only inline history.
        }
        return rows;
    }

    private static HistoryEntries SelectHistory(IReadOnlyList<StoredEntry>? rows, JsonElement[] inline)
    {
        if (rows is null || rows.Count == 0 || rows.Count != inline.Length) return new(inline, false);
        for (var index = 0; index < rows.Count; index++)
            if (rows[index].Sequence != index || rows[index].Json != inline[index].GetRawText())
                return new(inline, false);

        // Parse only after equality: corrupt, incomplete and stale mirrors fall back to inline.
        return new(rows.Select(row =>
        {
            using var entry = JsonDocument.Parse(row.Json);
            return entry.RootElement.Clone();
        }).ToArray(), true);
    }

    private static JsonElement[] Elements(JsonElement value) =>
        value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null ? [] : value.EnumerateArray().ToArray();

    private static JsonElement[] Inline(JsonElement task, string property) =>
        task.TryGetProperty(property, out var value) ? Elements(value) : [];

    internal async Task<GoalSnapshot?> HydrateAsync(SqliteConnection connection, GoalSnapshot? snapshot,
        CancellationToken token = default)
    {
        // Leave malformed task collections to the existing domain validation.
        if (snapshot is null || snapshot.IsMetadataOnly || snapshot.Tasks is null) return snapshot;
        var rows = await ReadRowsAsync(connection, snapshot.Id, null, token);
        var tasks = new List<TaskSnapshot>();
        foreach (var task in snapshot.Tasks)
        {
            var history = new TaskHistory(
                SelectHistory(rows.GetValueOrDefault((task.Id, "verification")),
                    Elements(JsonSerializer.SerializeToElement(task.VerificationHistory, options))),
                SelectHistory(rows.GetValueOrDefault((task.Id, "dispatch")),
                    Elements(JsonSerializer.SerializeToElement(task.DispatchHistory, options))));
            tasks.Add(task with
            {
                VerificationHistory = history.Verification.FromTable
                    ? history.Verification.Entries.Select(entry => entry.Deserialize<TaskVerificationSnapshot>(options)!).ToArray()
                    : task.VerificationHistory,
                DispatchHistory = history.Dispatch.FromTable
                    ? history.Dispatch.Entries.Select(entry => entry.Deserialize<TaskDispatchSnapshot>(options)!).ToArray()
                    : task.DispatchHistory
            });
        }
        return snapshot with { Tasks = tasks };
    }

    // A test diagnostic compares physical rows as well as accessor results, including surplus/removed-task rows.
    internal async Task<bool> IsConsistentAsync(SqliteConnection connection, string goalId, string goalJson,
        CancellationToken token = default)
    {
        using var document = JsonDocument.Parse(goalJson);
        var expected = new List<(string Task, string Kind, int Sequence, string Json)>();
        foreach (var task in document.RootElement.GetProperty("Tasks").EnumerateArray())
            foreach (var (property, kind) in Kinds)
                expected.AddRange(Inline(task, property).Select((entry, index) =>
                    (task.GetProperty("Id").GetString()!, kind, index, entry.GetRawText())));
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT task_id, kind, sequence, entry_json FROM goal_task_history WHERE goal_id = $goal ORDER BY task_id, kind, sequence";
        command.Parameters.AddWithValue("$goal", goalId);
        using var reader = await command.ExecuteReaderAsync(token);
        var actual = new List<(string Task, string Kind, int Sequence, string Json)>();
        while (await reader.ReadAsync(token)) actual.Add((reader.GetString(0), reader.GetString(1), reader.GetInt32(2), reader.GetString(3)));
        return expected.OrderBy(row => row.Task, StringComparer.Ordinal).ThenBy(row => row.Kind, StringComparer.Ordinal)
            .ThenBy(row => row.Sequence).SequenceEqual(actual);
    }

    internal static bool HasUsableCachedHistories(ProviderUsageTaskEvidence task) =>
        task.DispatchHistory is not null && task.VerificationHistory is not null;

    internal delegate bool InvalidVerification(ref Utf8JsonReader reader);

    // Streaming inline adapter for the backlog shape probe. Preserve its allocation-free legacy path.
    internal static bool TryReadInlineShape(ref Utf8JsonReader reader, InvalidVerification invalidVerification,
        out bool requiresReconstruction)
    {
        requiresReconstruction = false;
        var verification = reader.ValueTextEquals(VerificationProperty);
        if (!verification && !reader.ValueTextEquals(DispatchProperty)) return false;
        reader.Read();
        if (reader.TokenType != JsonTokenType.StartArray)
        {
            SkipInlineValue(ref reader);
            return true;
        }
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            requiresReconstruction |= verification ? invalidVerification(ref reader) : reader.TokenType == JsonTokenType.Null;
            SkipInlineValue(ref reader);
        }
        return true;
    }

    private static void SkipInlineValue(ref Utf8JsonReader reader)
    {
        if (!reader.TrySkip()) throw new JsonException("Incomplete similarity projection value.");
    }

    internal async Task<bool> HasRowsAsync(SqliteConnection connection, string goalId, CancellationToken token)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS (SELECT 1 FROM goal_task_history WHERE goal_id = $goal)";
        command.Parameters.AddWithValue("$goal", goalId);
        try { return (long)(await command.ExecuteScalarAsync(token))! != 0; }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 1 &&
            ex.Message.Contains("no such table: goal_task_history", StringComparison.OrdinalIgnoreCase)) { return false; }
    }

    internal async Task<bool> RequiresReconstructionAsync(SqliteConnection connection, string goalId, Stream stream,
        InvalidVerification invalidVerification, CancellationToken token)
    {
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: token);
        var invalid = false;
        foreach (var task in document.RootElement.GetProperty("Tasks").EnumerateArray())
        {
            if (task.ValueKind == JsonValueKind.Null) continue; // The existing shape probe owns null-task validation.
            var history = await ReadAsync(connection, goalId, task, token);
            invalid |= history.Dispatch.Entries.Any(entry => entry.ValueKind == JsonValueKind.Null);
            foreach (var entry in history.Verification.Entries)
                invalid |= InvalidEntry(entry, invalidVerification);
        }
        return invalid;
    }

    private static bool InvalidEntry(JsonElement entry, InvalidVerification invalidVerification)
    {
        var reader = new Utf8JsonReader(System.Text.Encoding.UTF8.GetBytes(entry.GetRawText()));
        reader.Read();
        return invalidVerification(ref reader);
    }
}
