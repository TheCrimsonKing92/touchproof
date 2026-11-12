using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Microsoft.Data.Sqlite;

namespace Mcg.AgentOrchestrator.Infrastructure;

// Disposable acceleration data: every miss uses the canonical snapshot projection.
internal static class ProviderHoldSeedEvidenceStore
{
    internal const int FormatVersion = 1;

    // Snapshot writers advance version/updated_at; archiving and recompression change archive metadata.
    // Keep this expression identical for cache validation and the atomic snapshot/marker read.
    internal static string MarkerSql(bool hasArchive) =>
        "CAST(g.version AS TEXT) || '|' || g.updated_at || '|' || " +
        (hasArchive ? "COALESCE(a.archived_at, '') || '|' || COALESCE(typeof(a.snapshot_json), '')" : "'|null'");

    internal static async Task<bool> HasArchiveAsync(SqliteConnection connection, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM sqlite_master WHERE type = 'table' AND name = 'goal_snapshot_archive'";
        return (long)(await command.ExecuteScalarAsync(token))! != 0;
    }

    internal static async Task<IReadOnlyList<ProviderUsageGoalEvidence>> ReadAsync(SqliteConnection connection,
        IReadOnlyCollection<GoalId> ids, JsonSerializerOptions options, CancellationToken cancellationToken,
        Action<GoalSnapshotReadObservation>? observer = null)
    {
        if (ids.Count == 0) return [];
        var result = new Dictionary<string, ProviderUsageGoalEvidence>(StringComparer.Ordinal);
        try
        {
            var hasArchive = await HasArchiveAsync(connection, cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = $"""
                SELECT g.id, e.evidence_json FROM goals g
                {(hasArchive ? "LEFT JOIN goal_snapshot_archive a ON a.goal_id = g.id" : "")}
                JOIN provider_hold_seed_evidence e ON e.goal_id = g.id
                WHERE g.id IN (SELECT value FROM json_each($ids))
                    AND e.format_version = $version AND e.marker = {MarkerSql(hasArchive)}
                ORDER BY g.id
                """;
            command.Parameters.AddWithValue("$ids", JsonSerializer.Serialize(ids.Select(id => id.Value)));
            command.Parameters.AddWithValue("$version", FormatVersion);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var id = reader.GetString(0);
                try
                {
                    if (reader.GetValue(1) is not string json) continue;
                    var evidence = JsonSerializer.Deserialize<ProviderUsageGoalEvidence>(json, options);
                    if (evidence is not null && evidence.GoalId == id && evidence.Tasks is not null &&
                        evidence.Timeline is not null && evidence.Tasks.All(task => task is not null &&
                            task.DispatchHistory is not null && task.VerificationHistory is not null) &&
                        evidence.Timeline.All(evt => evt is not null && evt.GoalId?.Value == id))
                        result[id] = evidence;
                }
                catch (Exception exception) when (exception is JsonException or NotSupportedException or InvalidOperationException)
                { /* Unusable rows are misses, never authoritative evidence. */ }
            }
        }
        catch (SqliteException) { result.Clear(); } // Missing/unreadable table: project every candidate.

        var missing = ids.Where(id => !result.ContainsKey(id.Value)).ToArray();
        var writes = new List<(ProviderUsageGoalEvidence Evidence, string Marker)>();
        try
        {
            var projected = await ProviderHoldSeedEvidenceReader.ReadSnapshotsAsync(connection, missing, options,
                cancellationToken, observer, (evidence, marker) => writes.Add((evidence, marker)));
            foreach (var evidence in projected) result[evidence.GoalId] = evidence;
        }
        finally
        {
            // ReadSnapshotsAsync has disposed its reader, including when a later snapshot failed.
            // Preserve successful projections without ever caching a failed one.
            await TryWriteAsync(connection, writes, options, cancellationToken);
        }
        return result.Values.OrderBy(evidence => evidence.GoalId, StringComparer.Ordinal).ToArray();
    }

    [StoreSchemaSource("orchestrator-state", "src/Mcg.AgentOrchestrator.Execution/Persistence/ProviderHoldSeedEvidenceStore.cs")]
    private static async Task CreateSchemaAsync(SqliteConnection connection, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.CommandTimeout = 1;
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS provider_hold_seed_evidence (
                goal_id TEXT PRIMARY KEY,
                marker TEXT NOT NULL,
                format_version INTEGER NOT NULL,
                evidence_json TEXT NOT NULL
            )
            """;
        await command.ExecuteNonQueryAsync(token);
    }

    private static async Task TryWriteAsync(SqliteConnection connection,
        IReadOnlyList<(ProviderUsageGoalEvidence Evidence, string Marker)> rows,
        JsonSerializerOptions options, CancellationToken token)
    {
        if (rows.Count == 0) return;
        var began = false;
        long? busyTimeout = null;
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandTimeout = 1;
            command.CommandText = "PRAGMA busy_timeout";
            busyTimeout = (long)(await command.ExecuteScalarAsync(token))!;
            command.CommandText = $"PRAGMA busy_timeout={StateDbConnectionFactory.FastFailBusyTimeoutMilliseconds}";
            await command.ExecuteNonQueryAsync(token);
            command.CommandText = "BEGIN IMMEDIATE";
            await command.ExecuteNonQueryAsync(token);
            began = true;
            await CreateSchemaAsync(connection, token);
            foreach (var (evidence, marker) in rows)
            {
                command.CommandText = """
                    INSERT INTO provider_hold_seed_evidence (goal_id, marker, format_version, evidence_json)
                    VALUES ($id, $marker, $version, $json)
                    ON CONFLICT(goal_id) DO UPDATE SET marker = excluded.marker,
                        format_version = excluded.format_version, evidence_json = excluded.evidence_json
                    """;
                command.Parameters.Clear();
                command.Parameters.AddWithValue("$id", evidence.GoalId);
                command.Parameters.AddWithValue("$marker", marker);
                command.Parameters.AddWithValue("$version", FormatVersion);
                command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(evidence, options));
                await command.ExecuteNonQueryAsync(token);
            }
            command.CommandText = "COMMIT";
            await command.ExecuteNonQueryAsync(token);
            began = false;
        }
        catch (SqliteException) { /* Best effort: the snapshot result remains usable. */ }
        finally
        {
            if (began)
            {
                await using var rollback = connection.CreateCommand();
                rollback.CommandTimeout = 1;
                rollback.CommandText = "ROLLBACK";
                try { await rollback.ExecuteNonQueryAsync(CancellationToken.None); }
                catch (SqliteException) { /* Cache rollback failure must not replace snapshot evidence/failure. */ }
            }
            if (busyTimeout is { } timeout)
            {
                await using var restore = connection.CreateCommand();
                restore.CommandText = $"PRAGMA busy_timeout={timeout.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
                try { await restore.ExecuteNonQueryAsync(CancellationToken.None); }
                catch (SqliteException) { /* Best effort, like the cache write itself. */ }
            }
        }
    }
}
