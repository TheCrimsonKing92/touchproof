using System.Globalization;
using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Microsoft.Data.Sqlite;

namespace Mcg.AgentOrchestrator.Infrastructure;

/// <summary>Retains original terminal snapshots while their hot rows hold only metadata.</summary>
public sealed class GoalSnapshotArchive(JsonSerializerOptions serializerOptions)
{
    // The caller initializes the schema and owns the connection and transaction covering the entire eligible set.
    public Task<int> ArchiveAsync(
        SqliteConnection connection, DateTimeOffset cutoff, long maxSnapshotBytes = 134_217_728,
        CancellationToken cancellationToken = default, bool compress = false) =>
        ArchiveAsync(connection, cutoff, maxSnapshotBytes, cancellationToken, compress, archivedGoalIds: null);

    internal async Task<int> ArchiveAsync(
        SqliteConnection connection, DateTimeOffset cutoff, long maxSnapshotBytes,
        CancellationToken cancellationToken, bool compress, ICollection<string>? archivedGoalIds)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxSnapshotBytes, 1L);
        var hasTimelineTable = await GoalTimelineStore.HasTableAsync(connection, cancellationToken);
        var candidates = new List<(string Id, long Bytes)>();
        await using (var select = connection.CreateCommand())
        {
            select.CommandText = $"""
                WITH candidates AS MATERIALIZED (
                SELECT id, CASE WHEN json_valid(snapshot_json) THEN {GoalTimelineStore.ScalarSql(hasTimelineTable,
                    "(SELECT MAX(julianday(json_extract(e.value, '$.OccurredAt'))) FROM json_each(goals.snapshot_json, '$.Timeline') AS e)",
                    "MAX(julianday(json_extract(e.value, '$.OccurredAt')))", alias: "e")} END AS terminal, length(snapshot_json) AS bytes FROM goals
                WHERE status IN ('{GoalStatus.Completed}', '{GoalStatus.Cancelled}', '{GoalStatus.Superseded}')
                  AND CASE WHEN json_valid(snapshot_json)
                      THEN COALESCE(json_extract(snapshot_json, '$.IsMetadataOnly'), 0) = 0
                        AND COALESCE(json_extract(snapshot_json, '$.CurrentHold.State') COLLATE NOCASE NOT IN
                            ('steward-owner-question', 'author-owner-question'), 1)
                        AND {TerminalOpenWorkIndex.OpenWorkFlagSql("status", "goals.snapshot_json")} = 0
                      ELSE 0 END
                  AND NOT EXISTS (SELECT 1 FROM goal_snapshot_archive WHERE goal_id = goals.id)
                )
                SELECT id, terminal, bytes FROM candidates
                WHERE terminal IS NOT NULL AND terminal < julianday($cutoff)
                ORDER BY terminal ASC, id ASC
                """;
            select.Parameters.AddWithValue("$cutoff", cutoff.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
            await using var reader = await select.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                candidates.Add((reader.GetString(0), reader.GetInt64(2)));
        }

        if (candidates.Count != 0)
            await EnsureProviderHoldDiscoveryColumnAsync(connection, cancellationToken);
        var archived = 0;
        long archivedBytes = 0;
        var archivedAt = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        foreach (var (id, bytes) in candidates)
        {
            if (archivedBytes >= maxSnapshotBytes)
                break;
            cancellationToken.ThrowIfCancellationRequested();
            await using var read = connection.CreateCommand();
            read.CommandText = "SELECT snapshot_json FROM goals WHERE id = $id";
            read.Parameters.AddWithValue("$id", id);
            var json = (string)(await read.ExecuteScalarAsync(cancellationToken))!;
            GoalSnapshot? snapshot;
            try
            {
                snapshot = GoalSnapshotByteAccounting.Deserialize<GoalSnapshot>(json, serializerOptions);
                snapshot = await new GoalTaskHistoryStore(serializerOptions)
                    .HydrateAsync(connection, snapshot, json, cancellationToken);
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException or ArgumentException or InvalidDataException)
            {
                continue;
            }
            if (snapshot is null || snapshot.IsMetadataOnly)
                continue;

            var stub = CreateMetadataSnapshot(snapshot);
            await using var copy = connection.CreateCommand();
            if (compress)
            {
                copy.CommandText = """
                    INSERT INTO goal_snapshot_archive (goal_id, snapshot_json, archived_at, provider_hold_empty_version)
                    VALUES ($id, $snapshot, $archived_at, $providerHoldEmpty)
                    ON CONFLICT(goal_id) DO NOTHING
                    """;
                copy.Parameters.Add("$snapshot", SqliteType.Blob).Value = GoalSnapshotArchiveCodec.Encode(json);
            }
            else
            {
                copy.CommandText = """
                INSERT INTO goal_snapshot_archive (goal_id, snapshot_json, archived_at, provider_hold_empty_version)
                SELECT id, snapshot_json, $archived_at, $providerHoldEmpty FROM goals WHERE id = $id
                ON CONFLICT(goal_id) DO NOTHING
                """;
            }
            copy.Parameters.AddWithValue("$id", id);
            copy.Parameters.AddWithValue("$archived_at", archivedAt);
            copy.Parameters.AddWithValue("$providerHoldEmpty", ProviderHoldSeedDiscovery.HasReplayEvidence(snapshot)
                ? (object)DBNull.Value : ProviderHoldEmptyVersion);
            if (await copy.ExecuteNonQueryAsync(cancellationToken) == 0)
                continue;

            await using var update = connection.CreateCommand();
            update.CommandText = "UPDATE goals SET snapshot_json = $stub WHERE id = $id";
            update.Parameters.AddWithValue("$id", id);
            update.Parameters.AddWithValue("$stub", GoalSnapshotByteAccounting.Serialize(stub, serializerOptions));
            var updated = await update.ExecuteNonQueryAsync(cancellationToken);
            archived += updated;
            if (updated > 0)
            {
                archivedBytes += bytes;
                archivedGoalIds?.Add(id);
            }
        }
        return archived;
    }

    // A versioned negative projection of the authoritative archived document and its histories.
    // NULL covers legacy archives and archives that still need provider-hold replay.
    internal const int ProviderHoldEmptyVersion = 1;

    internal static async Task<bool> HasProviderHoldDiscoveryColumnAsync(SqliteConnection connection, CancellationToken token)
    {
        using var command = connection.CreateCommand();
        command.CommandTimeout = 1;
        command.CommandText = "SELECT count(*) FROM pragma_table_info('goal_snapshot_archive') WHERE name = 'provider_hold_empty_version'";
        return (long)(await command.ExecuteScalarAsync(token))! != 0;
    }

    internal static async Task EnsureProviderHoldDiscoveryColumnAsync(SqliteConnection connection, CancellationToken token)
    {
        if (await HasProviderHoldDiscoveryColumnAsync(connection, token)) return;
        using var command = connection.CreateCommand();
        command.CommandTimeout = 1;
        command.CommandText = "ALTER TABLE goal_snapshot_archive ADD COLUMN provider_hold_empty_version INTEGER";
        await command.ExecuteNonQueryAsync(token);
    }

    internal static async Task BackfillProviderHoldDiscoveryAsync(SqliteConnection connection,
        IReadOnlyDictionary<string, string> emptyArchives, CancellationToken token)
    {
        if (emptyArchives.Count == 0) return;
        try
        {
            await EnsureProviderHoldDiscoveryColumnAsync(connection, token);
            using var command = connection.CreateCommand();
            command.CommandTimeout = 1;
            command.CommandText = """
                UPDATE goal_snapshot_archive SET provider_hold_empty_version = $version
                FROM json_each($archives) archived
                WHERE goal_id = archived.key AND archived_at = archived.value
                    AND provider_hold_empty_version IS NOT $version
                """;
            command.Parameters.AddWithValue("$version", ProviderHoldEmptyVersion);
            command.Parameters.AddWithValue("$archives", JsonSerializer.Serialize(emptyArchives));
            await command.ExecuteNonQueryAsync(token);
        }
        catch (SqliteException)
        {
            // A legacy archive without a projection remains eligible for discovery.
        }
    }

    public async Task<GoalSnapshot?> HydrateAsync(
        SqliteConnection connection, GoalSnapshot? snapshot, CancellationToken cancellationToken = default,
        Action<GoalSnapshotReadObservation>? readObserver = null)
    {
        if (snapshot is not { IsMetadataOnly: true })
            return snapshot;

        var (json, utf8Bytes) = await ReadDocumentAsync(connection, snapshot.Id, cancellationToken);
        if (json is null) return snapshot;
        readObserver?.Invoke(new("archive-attempt", snapshot.Id, json.Length));
        var hydrated = GoalSnapshotByteAccounting.Deserialize<GoalSnapshot>(json, serializerOptions, utf8Bytes);
        if (hydrated is not null)
            readObserver?.Invoke(new("archive-deserialized", snapshot.Id, json.Length));
        return await new GoalTimelineStore(serializerOptions).HydrateAsync(connection, hydrated, json, cancellationToken, utf8Bytes);
    }

    internal static async Task<(string? Json, long? Utf8Bytes)> ReadDocumentAsync(
        SqliteConnection connection, string goalId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT snapshot_json FROM goal_snapshot_archive WHERE goal_id = $id";
        command.Parameters.AddWithValue("$id", goalId);
        try
        {
            var json = GoalSnapshotArchiveCodec.Decode(await command.ExecuteScalarAsync(cancellationToken), out var utf8Bytes);
            return (json, utf8Bytes);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 1 &&
            ex.Message.Contains("no such table: goal_snapshot_archive", StringComparison.OrdinalIgnoreCase))
        {
            return default;
        }
    }

    private static GoalSnapshot CreateMetadataSnapshot(GoalSnapshot snapshot)
    {
        var seed = new GoalSnapshot(
            snapshot.Id, snapshot.Objective, snapshot.Status, [], [], IsMetadataOnly: true,
            ResultCommit: snapshot.Tasks.Select(task => task.LastDispatch?.ResultCommit)
                .LastOrDefault(commit => !string.IsNullOrEmpty(commit)),
            CreatedAt: snapshot.Timeline.FirstOrDefault()?.OccurredAt,
            TerminatedAt: snapshot.Timeline.LastOrDefault()?.OccurredAt);
        // Public kernel APIs reach Goal.FromSnapshot and Goal.ToSnapshot in Core.
        var kernel = AgentOrchestratorKernel.FromSnapshot(new OrchestratorSnapshot([seed], []));
        return kernel.ExportGoalSnapshot(new GoalId(snapshot.Id));
    }
}
