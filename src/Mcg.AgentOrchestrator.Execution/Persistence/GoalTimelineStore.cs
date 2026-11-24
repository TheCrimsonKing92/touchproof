using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Microsoft.Data.Sqlite;

namespace Mcg.AgentOrchestrator.Infrastructure;

/// <summary>
/// Storage-only TimelineInTable is a non-negative count; absent/null keeps inline authority.
/// Marked documents require exactly that many rows, at contiguous zero-based sequences.
/// This build writes the member and timeline rows. The writer step may be adopted only
/// after a build containing this reader has been adopted AND one later build has been adopted.
/// Level 2 fences older writers; a manual downgrade is safe only while no document is marked.
/// A build older than step 4a reads a marked Timeline as empty and cannot be a revert target.
/// UpsertGoalRowAsync writes rows and proves exact serialized-text equality in the caller's
/// existing write transaction before emitting the count. Failed proof keeps Timeline inline
/// and removes leftover rows. Matching prefixes are preserved; all other layouts are replaced.
///
/// Reader inventory: search over src and scripts, excluding bin, obj, .scratch and prototype
/// state: Timeline; TimelineInTable; Deserialize.*GoalSnapshot; snapshot_json; json_extract;
/// json_each; goal_snapshot_archive; goal_timeline (including $.Timeline and Timeline[ spellings).
/// Hydrating paths: GoalTaskHistoryStore.HydrateAsync covers LoadFromConnectionAsync
/// (full/selected/status/board, model-fit backfill, goal creation), LoadGoalAsync, both prefix
/// lookups, LoadGoalSnapshotAndVersionAsync (transaction/merge baseline),
/// LoadGoalStateAndVersionAsync, BacklogReads suspect replay and archive creation.
/// EpicReads.ReadEpicGoalsAsync and GoalSnapshotArchive.HydrateAsync also use this accessor.
/// Bypass readers (Persistence unless qualified):
/// - Repository metadata and conduct-loop SQL, EpicReads metadata, AcceptanceQueueReads and
///   archive candidate age use ScalarSql, keeping legacy expressions when the table is absent.
///   SQL projections do not validate counts; hydrating readers own strict validation.
/// - ReadTerminalMetadata, AttentionReads, ProviderHoldSeedEvidenceReader and
///   OwnerDigestTimelineReader use ReadAsync, including strict validation of marked rows.
/// - BacklogReads projection and diagnostic fallback resolve the marker before latest-time
///   selection; suspect replay also hydrates. ProviderHoldSeedEvidenceStore caches resolved events.
/// - Archive stubs derive timestamps from hydrated events. Archive copies original text verbatim,
///   retains rows, and restore validates the archived marker. Defective goals remain hot.
///   Recompression preserves text; StateDatabaseOfflineConversion copies text and rows.
///   Never prune rows belonging to an archive. Leftover rows after an inline save are not
///   authoritative: the writer compares their exact text and layout before preserving a prefix.
/// - TryReadGoalSnapshot/quarantine listing consume no Timeline (shape/identity only).
///   Metadata-only hot stubs carry their own timestamps. GoalCreation dependency reads,
///   App/ReadModels/EpicPlanGoalClaimSnapshot (coverage), scripts/Invoke-IsolatedDotnet.ps1
///   (task role), and the operator utility status/diagnostics/set-goal-status consume no Timeline.
///   SqliteWorkerOccupancyReader and TerminalOpenWorkIndex inspect only task/status fields.
/// - scripts/OrchestratorSqliteTools/Program.cs requeue validates marked rows, appends its
///   event and increments the count within its existing write transaction; inline remains supported.
/// Document and rows share a read snapshot: ReadTerminalMetadata retains its document reader
/// through the row reads, as do the sparse and hydrating readers; SQL projections use one statement.
/// </summary>
internal sealed class GoalTimelineStore(JsonSerializerOptions options)
{
    internal const string MarkerProperty = "TimelineInTable";

    // The caller owns the write transaction. Never create the table here: absence means inline.
    internal async Task<(int? MarkerCount, bool Changed)> WriteAsync(SqliteConnection connection,
        GoalSnapshot goal, CancellationToken token)
    {
        if (!await HasTableAsync(connection, token)) return (null, false);
        var entries = goal.Timeline?.Select(entry => JsonSerializer.Serialize(entry, options)).ToArray();
        var rowsJson = JsonSerializer.Serialize(entries ?? [], options);
        var prefix = 0;
        var matches = entries is not null;
        using (var read = connection.CreateCommand())
        {
            read.CommandText = """
                SELECT COUNT(*), COALESCE(MAX(sequence), -1),
                    (SELECT COUNT(*) FROM json_each($entries) e CROSS JOIN goal_timeline t
                     WHERE t.goal_id = $id AND t.sequence = CAST(e.key AS INTEGER)
                       AND t.entry_json = e.value COLLATE BINARY)
                FROM goal_timeline WHERE goal_id = $id
                """;
            read.Parameters.AddWithValue("$id", goal.Id);
            read.Parameters.AddWithValue("$entries", rowsJson);
            using var reader = await read.ExecuteReaderAsync(token);
            await reader.ReadAsync(token);
            prefix = reader.GetInt32(0);
            matches &= reader.GetInt64(1) == prefix - 1L && reader.GetInt64(2) == prefix;
        }
        async Task<int> DeleteAsync()
        {
            using var delete = connection.CreateCommand();
            delete.CommandText = "DELETE FROM goal_timeline WHERE goal_id = $id";
            delete.Parameters.AddWithValue("$id", goal.Id);
            var removed = await delete.ExecuteNonQueryAsync(token);
            delete.CommandText = "SELECT EXISTS (SELECT 1 FROM goal_timeline WHERE goal_id = $id)";
            if ((long)(await delete.ExecuteScalarAsync(token))! != 0)
                throw Error(goal.Id, "could not remove leftover rows");
            return removed;
        }
        var changed = false;
        if (!matches)
        {
            changed = await DeleteAsync() > 0;
            prefix = 0;
        }
        if (entries is null) return (null, changed);
        if (prefix < entries.Length)
        {
            using var insert = connection.CreateCommand();
            insert.CommandText = """
                INSERT INTO goal_timeline(goal_id, sequence, entry_json)
                SELECT $id, CAST(key AS INTEGER), value FROM json_each($entries)
                WHERE CAST(key AS INTEGER) >= $prefix
                """;
            insert.Parameters.AddWithValue("$id", goal.Id);
            insert.Parameters.AddWithValue("$prefix", prefix);
            insert.Parameters.AddWithValue("$entries", rowsJson);
            changed |= await insert.ExecuteNonQueryAsync(token) > 0;
        }
        // Re-read actual rows after writes (including trigger effects), inside this transaction.
        using var proof = connection.CreateCommand();
        proof.CommandText = """
            SELECT (SELECT COUNT(*) FROM goal_timeline WHERE goal_id = $id) = $count
              AND (SELECT COUNT(*) FROM json_each($entries) e CROSS JOIN goal_timeline t
                   WHERE t.goal_id = $id AND t.sequence = CAST(e.key AS INTEGER)
                     AND t.entry_json = e.value COLLATE BINARY) = $count
            """;
        proof.Parameters.AddWithValue("$id", goal.Id);
        proof.Parameters.AddWithValue("$count", entries.Length);
        proof.Parameters.AddWithValue("$entries", rowsJson);
        if ((long)(await proof.ExecuteScalarAsync(token))! == 1) return (entries.Length, changed);
        changed |= await DeleteAsync() > 0;
        return (null, changed);
    }

    [StoreSchemaSource("orchestrator-state", "src/Mcg.AgentOrchestrator.Execution/Persistence/GoalTimelineStore.cs")]
    internal static void CreateSchema(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS goal_timeline (
                goal_id TEXT NOT NULL,
                sequence INTEGER NOT NULL CHECK (sequence >= 0),
                entry_json TEXT NOT NULL,
                PRIMARY KEY (goal_id, sequence)
            )
            """;
        command.ExecuteNonQuery();
    }

    private static InvalidDataException Error(string id, string defect, Exception? inner = null) =>
        new($"Goal '{id}' timeline: {defect}.", inner);

    private static int? ReadMarker(string id, JsonElement marker)
    {
        if (marker.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) return null;
        if (marker.ValueKind == JsonValueKind.Number && marker.TryGetInt32(out var count) && count >= 0)
            return count;
        throw Error(id, "invalid TimelineInTable count");
    }

    internal async Task<(int Count, DateTimeOffset? FirstOccurredAt)> ReadTickSummaryAsync(
        SqliteConnection connection, GoalSnapshot snapshot, JsonElement marker, CancellationToken token)
    {
        if (ReadMarker(snapshot.Id, marker) is not { } expected)
            return (snapshot.Timeline.Count, snapshot.Timeline.MinBy(evt => evt.OccurredAt)?.OccurredAt);

        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*), COALESCE(MAX(sequence), -1)
            FROM goal_timeline WHERE goal_id = $id
            """;
        command.Parameters.AddWithValue("$id", snapshot.Id);
        try
        {
            using (var reader = await command.ExecuteReaderAsync(token))
            {
                await reader.ReadAsync(token);
                var actual = reader.GetInt64(0);
                if (actual != expected) throw Error(snapshot.Id, $"expected {expected} rows, found {actual}");
                if (reader.GetInt64(1) != actual - 1) throw Error(snapshot.Id, "sequence gap");
            }

            if (expected == 0 || snapshot.CreatedAt is not null || snapshot.BriefVersions is { Count: > 0 })
                return (expected, null);

            command.CommandText = """
                SELECT json_extract(entry_json, '$.OccurredAt') FROM goal_timeline
                WHERE goal_id = $id ORDER BY julianday(json_extract(entry_json, '$.OccurredAt')), sequence LIMIT 1
                """;
            var first = (string?)await command.ExecuteScalarAsync(token);
            return (expected, first is null ? null : JsonSerializer.Deserialize<DateTimeOffset>(
                JsonSerializer.Serialize(first)));
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 1 &&
            ex.Message.Contains("no such table: goal_timeline", StringComparison.OrdinalIgnoreCase))
        { throw Error(snapshot.Id, "missing goal_timeline table", ex); }
    }

    internal Task<JsonElement[]> ReadAsync(SqliteConnection? connection, string id, JsonElement root,
        CancellationToken token = default) => ReadAsync(connection, id,
            root.TryGetProperty("Timeline", out var inline) ? inline : default,
            root.TryGetProperty(MarkerProperty, out var marker) ? marker : default, token);

    internal async Task<JsonElement[]> ReadAsync(SqliteConnection? connection, string id, JsonElement inline,
        JsonElement marker, CancellationToken token = default, string? includeSql = null,
        Action<int>? observePayload = null)
    {
        var count = ReadMarker(id, marker);
        if (count is null)
            return inline.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
                ? [] : inline.EnumerateArray().ToArray();
        if (connection is null) throw Error(id, "no timeline connection");
        var entries = new List<JsonElement>();
        var seen = 0;
        using var command = connection.CreateCommand();
        // Validate every row. For excluded events, elide text that can deserialize as a
        // string without error. Keep surrogate escapes so the serializer can check them.
        command.CommandText = includeSql is null
            ? "SELECT sequence, entry_json, 1 FROM goal_timeline WHERE goal_id = $id ORDER BY sequence"
            : $"""
            WITH selection AS (
                SELECT sequence, entry_json, CASE
                    WHEN NOT json_valid(entry_json) THEN 1
                    WHEN json_type(entry_json) <> 'object' OR ({includeSql}) THEN 1
                    ELSE 0 END AS included
                FROM goal_timeline WHERE goal_id = $id
            )
            SELECT sequence, CASE
                WHEN included THEN entry_json
                WHEN json_type(entry_json, '$.Message') = 'text' AND entry_json NOT GLOB '*\u[dD][89a-fA-F]*'
                    THEN json_replace(entry_json, '$.Message', '')
                ELSE entry_json END, included
            FROM selection ORDER BY sequence
            """;
        command.Parameters.AddWithValue("$id", id);
        try
        {
            using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                var sequence = seen++;
                if (reader.GetInt64(0) != sequence) throw Error(id, $"sequence gap at {sequence}");
                try
                {
                    var json = reader.GetString(1);
                    observePayload?.Invoke(json.Length);
                    using var document = JsonDocument.Parse(json);
                    if (document.RootElement.ValueKind != JsonValueKind.Object ||
                        document.RootElement.Deserialize<ProgressEventSnapshot>(options) is null)
                        throw new JsonException("Timeline entry must be an object.");
                    if (reader.GetBoolean(2)) entries.Add(document.RootElement.Clone());
                }
                catch (JsonException ex) { throw Error(id, $"unparseable entry at {sequence}", ex); }
            }
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 1 &&
            ex.Message.Contains("no such table: goal_timeline", StringComparison.OrdinalIgnoreCase))
        { throw Error(id, "missing goal_timeline table", ex); }
        if (seen != count) throw Error(id, $"expected {count} rows, found {seen}");
        return entries.ToArray();
    }

    internal async Task<GoalSnapshot> ApplyAsync(SqliteConnection connection, GoalSnapshot snapshot,
        JsonElement marker, CancellationToken token)
    {
        if (ReadMarker(snapshot.Id, marker) is null) return snapshot;
        var entries = await ReadAsync(connection, snapshot.Id, default, marker, token);
        return snapshot with { Timeline = entries.Select(entry => entry.Deserialize<ProgressEventSnapshot>(options)!).ToArray() };
    }

    internal async Task<GoalSnapshot?> HydrateAsync(SqliteConnection connection, GoalSnapshot? snapshot,
        string text, CancellationToken token = default, long? utf8Bytes = null)
    {
        if (snapshot is null || snapshot.IsMetadataOnly) return snapshot;
        var fields = GoalSnapshotByteAccounting.Deserialize<TimelineDocument>(text, options, utf8Bytes);
        if (fields?.IsMetadataOnly == true)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT snapshot_json FROM goal_snapshot_archive WHERE goal_id = $id";
            command.Parameters.AddWithValue("$id", snapshot.Id);
            text = GoalSnapshotArchiveCodec.Decode(await command.ExecuteScalarAsync(token), out utf8Bytes)
                ?? throw Error(snapshot.Id, "missing archive");
            fields = GoalSnapshotByteAccounting.Deserialize<TimelineDocument>(text, options, utf8Bytes);
        }
        return await ApplyAsync(connection, snapshot, fields?.TimelineInTable ?? default, token);
    }

    internal sealed record TimelineDocument(bool IsMetadataOnly, JsonElement TimelineInTable);

    internal static async Task<bool> HasTableAsync(SqliteConnection connection, CancellationToken token = default)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS (SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'goal_timeline')";
        return (long)(await command.ExecuteScalarAsync(token))! != 0;
    }

    // Fragments come only from trusted source expressions. Inline arms retain their existing SQL.
    internal static string ScalarSql(bool hasTable, string inline, string select,
        string alias = "evt", string? predicate = null, bool? descending = null)
    {
        if (!hasTable) return inline;
        string Rows(string expression) => expression.Replace(alias + ".value", "t.entry_json", StringComparison.Ordinal);
        return $"""
            CASE WHEN json_type(goals.snapshot_json, '$.TimelineInTable') = 'integer'
              THEN (SELECT {Rows(select)} FROM goal_timeline t WHERE t.goal_id = goals.id
                {(predicate is null ? "" : "AND " + Rows(predicate))}
                {(descending is null ? "" : "ORDER BY t.sequence " + (descending.Value ? "DESC" : "ASC") + " LIMIT 1")})
              ELSE {inline} END
            """;
    }

    internal static string FirstSql(bool hasTable) => ScalarSql(hasTable,
        "json_extract(goals.snapshot_json, '$.Timeline[0].OccurredAt')",
        "json_extract(evt.value, '$.OccurredAt')", descending: false);

    internal static string OrderedInstantSql(bool hasTable, bool descending = false) => ScalarSql(hasTable,
        $"(SELECT json_extract(evt.value, '$.OccurredAt') FROM json_each(goals.snapshot_json, '$.Timeline') AS evt ORDER BY CAST(evt.key AS INTEGER) {(descending ? "DESC" : "ASC")} LIMIT 1)",
        "json_extract(evt.value, '$.OccurredAt')", descending: descending);
}
