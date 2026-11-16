using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Microsoft.Data.Sqlite;

namespace Mcg.AgentOrchestrator.Infrastructure;

/// <summary>
/// Storage-only TimelineInTable is a non-negative count; absent/null keeps inline authority.
/// Marked documents require exactly that many rows, at contiguous zero-based sequences.
/// This build never writes the member or timeline rows. The writer step may be adopted only
/// after a build containing this reader has been adopted AND one later build has been adopted.
/// Level 2 fences older writers; a manual downgrade is safe only while no document is marked.
///
/// Reader inventory: search over src and scripts, excluding bin, obj, .scratch and prototype
/// state: Timeline; Deserialize.*GoalSnapshot; snapshot_json; json_extract; json_each;
/// goal_snapshot_archive (including quoted Timeline, $.Timeline and Timeline[ spellings).
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
///   authoritative: the later writer must replace them rather than append onto them.
/// - TryReadGoalSnapshot/quarantine listing consume no Timeline (shape/identity only).
///   Metadata-only hot stubs carry their own timestamps. GoalCreation dependency reads,
///   App/ReadModels/EpicPlanGoalClaimSnapshot (coverage), scripts/Invoke-IsolatedDotnet.ps1
///   (task role), and the operator utility status/diagnostics/set-goal-status consume no Timeline.
/// - scripts/OrchestratorSqliteTools/Program.cs requeue requires an inline Timeline array,
///   rejects marked documents before UPDATE and rolls back; it must learn row append/count
///   updates before the writer step. No utility write changes are needed in this reader step.
/// Document and rows must share a read snapshot when writers begin. ReadTerminalMetadata's
/// separate statements are safe in this reader-only step; the writer must close that seam.
/// </summary>
internal sealed class GoalTimelineStore(JsonSerializerOptions options)
{
    internal const string MarkerProperty = "TimelineInTable";

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

    internal Task<JsonElement[]> ReadAsync(SqliteConnection? connection, string id, JsonElement root,
        CancellationToken token = default) => ReadAsync(connection, id,
            root.TryGetProperty("Timeline", out var inline) ? inline : default,
            root.TryGetProperty(MarkerProperty, out var marker) ? marker : default, token);

    internal async Task<JsonElement[]> ReadAsync(SqliteConnection? connection, string id, JsonElement inline,
        JsonElement marker, CancellationToken token = default)
    {
        var count = ReadMarker(id, marker);
        if (count is null)
            return inline.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
                ? [] : inline.EnumerateArray().ToArray();
        if (connection is null) throw Error(id, "no timeline connection");
        var entries = new List<JsonElement>();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT sequence, entry_json FROM goal_timeline WHERE goal_id = $id ORDER BY sequence";
        command.Parameters.AddWithValue("$id", id);
        try
        {
            using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                if (reader.GetInt64(0) != entries.Count) throw Error(id, $"sequence gap at {entries.Count}");
                try
                {
                    using var document = JsonDocument.Parse(reader.GetString(1));
                    if (document.RootElement.ValueKind != JsonValueKind.Object ||
                        document.RootElement.Deserialize<ProgressEventSnapshot>(options) is null)
                        throw new JsonException("Timeline entry must be an object.");
                    entries.Add(document.RootElement.Clone());
                }
                catch (JsonException ex) { throw Error(id, $"unparseable entry at {entries.Count}", ex); }
            }
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 1 &&
            ex.Message.Contains("no such table: goal_timeline", StringComparison.OrdinalIgnoreCase))
        { throw Error(id, "missing goal_timeline table", ex); }
        if (entries.Count != count) throw Error(id, $"expected {count} rows, found {entries.Count}");
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
        string text, CancellationToken token = default)
    {
        if (snapshot is null || snapshot.IsMetadataOnly) return snapshot;
        var fields = JsonSerializer.Deserialize<TimelineDocument>(text, options);
        if (fields?.IsMetadataOnly == true)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT snapshot_json FROM goal_snapshot_archive WHERE goal_id = $id";
            command.Parameters.AddWithValue("$id", snapshot.Id);
            text = GoalSnapshotArchiveCodec.Decode(await command.ExecuteScalarAsync(token))
                ?? throw Error(snapshot.Id, "missing archive");
            fields = JsonSerializer.Deserialize<TimelineDocument>(text, options);
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
