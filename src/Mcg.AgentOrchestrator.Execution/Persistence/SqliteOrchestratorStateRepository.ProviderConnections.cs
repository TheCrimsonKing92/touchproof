using System.Globalization;
using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Microsoft.Data.Sqlite;

namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed partial class SqliteOrchestratorStateRepository
{
    [StoreSchemaSource("orchestrator-state", "src/Mcg.AgentOrchestrator.Execution/Persistence/SqliteOrchestratorStateRepository.ProviderConnections.cs")]
    internal static void ApplyProviderConnectionsSchema(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS provider_connection_observations (
                profile_id TEXT NOT NULL,
                execution_context INTEGER NOT NULL,
                evidence_source INTEGER NOT NULL,
                result INTEGER NOT NULL,
                authentication_mode INTEGER NOT NULL,
                observed_at TEXT NOT NULL,
                PRIMARY KEY (profile_id, execution_context)
            );
            CREATE UNIQUE INDEX IF NOT EXISTS ux_provider_connection_observations_identity
                ON provider_connection_observations (profile_id COLLATE mcg_profile_identity, execution_context);
            """;
        command.ExecuteNonQuery();
    }

    public async Task SaveProviderConnectionObservationAsync(string profileId, ProviderConnectionObservation observation,
        CancellationToken cancellationToken = default)
    {
        ValidateConnectionObservation(profileId, observation);
        var write = await BeginWriteAsync(nameof(SaveProviderConnectionObservationAsync), cancellationToken);
        await using var connection = write.Connection;
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO provider_connection_observations
                    (profile_id, execution_context, evidence_source, result, authentication_mode, observed_at)
                VALUES ($profile, $context, $source, $result, $mode, $at)
                ON CONFLICT(profile_id COLLATE mcg_profile_identity, execution_context) DO UPDATE SET
                    profile_id = excluded.profile_id,
                    evidence_source = excluded.evidence_source, result = excluded.result,
                    authentication_mode = excluded.authentication_mode, observed_at = excluded.observed_at
                WHERE excluded.observed_at >= provider_connection_observations.observed_at
                """;
            command.Parameters.AddWithValue("$profile", profileId);
            command.Parameters.AddWithValue("$context", (int)observation.ExecutionContext);
            command.Parameters.AddWithValue("$source", (int)observation.EvidenceSource);
            command.Parameters.AddWithValue("$result", (int)observation.Result);
            command.Parameters.AddWithValue("$mode", (int)observation.AuthenticationMode);
            command.Parameters.AddWithValue("$at", observation.ObservedAt!.Value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
            await command.ExecuteNonQueryAsync(cancellationToken);
            await RunNonQueryAsync(connection, "COMMIT", cancellationToken);
            write.Telemetry.Emit("commit");
        }
        catch (Exception exception)
        {
            try { await RunNonQueryAsync(connection, "ROLLBACK", CancellationToken.None); } catch { }
            write.Telemetry.Emit("rollback", exception);
            throw;
        }
    }

    private static void ValidateConnectionObservation(string profileId, ProviderConnectionObservation observation)
    {
        if (string.IsNullOrWhiteSpace(profileId) || observation.ObservedAt is null || observation.IsHistorical ||
            !Enum.IsDefined(observation.Result) || !Enum.IsDefined(observation.AuthenticationMode))
            throw new InvalidDataException("Invalid provider connection metadata.");
        var hostSource = observation.EvidenceSource is ProviderConnectionSource.CodexLoginStatus or ProviderConnectionSource.ClaudeAuthStatus ||
            observation.EvidenceSource == ProviderConnectionSource.None &&
            observation.Result is ProviderConnectionResult.Unknown or ProviderConnectionResult.Unsupported;
        var host = observation.ExecutionContext == ProviderConnectionContext.Host && hostSource &&
            (observation.Result is ProviderConnectionResult.Unknown or ProviderConnectionResult.SignedIn or ProviderConnectionResult.SignedOut or
                ProviderConnectionResult.Expired or ProviderConnectionResult.ToolMissing or ProviderConnectionResult.TimedOut or
                ProviderConnectionResult.Unsupported or ProviderConnectionResult.OutputLimited ||
            observation is { EvidenceSource: ProviderConnectionSource.CodexLoginStatus,
                Result: ProviderConnectionResult.MaterialPresent, AuthenticationMode: ProviderConnectionAuthMode.ApiKey });
        var material = observation.ExecutionContext == ProviderConnectionContext.WorkerCredentialMaterial &&
            observation.EvidenceSource == ProviderConnectionSource.ClaudePreflight &&
            observation.Result is ProviderConnectionResult.MaterialPresent or ProviderConnectionResult.MaterialUnavailable;
        if (!host && !material) throw new InvalidDataException("Invalid provider connection evidence context.");
    }

    public async Task<IReadOnlyList<ProviderConnectionObservation>> ReadProviderConnectionEvidenceAsync(string profileId,
        CancellationToken cancellationToken = default)
        => (await ReadProviderConnectionEvidenceAsync(new[] { profileId }, cancellationToken))[profileId];

    public async Task<IReadOnlyDictionary<string, IReadOnlyList<ProviderConnectionObservation>>> ReadProviderConnectionEvidenceAsync(
        IReadOnlyList<string> profileIds, CancellationToken cancellationToken = default)
    {
        var result = profileIds.ToDictionary(id => id, _ => new List<ProviderConnectionObservation>(), StringComparer.OrdinalIgnoreCase);
        if (profileIds.Count == 0) return new Dictionary<string, IReadOnlyList<ProviderConnectionObservation>>();
        await using var connection = OpenConnection();
        // Keep task marker documents and their table rows in the same read snapshot.
        await RunNonQueryAsync(connection, "BEGIN", cancellationToken);
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT EXISTS(SELECT 1 FROM sqlite_master WHERE type='table' AND name='provider_connection_observations')";
            if ((long)(await command.ExecuteScalarAsync(cancellationToken))! != 0)
            {
                command.CommandText = """
                    SELECT execution_context, evidence_source, result, authentication_mode, observed_at, profile_id
                    FROM (
                        SELECT *, ROW_NUMBER() OVER (
                            PARTITION BY profile_id COLLATE mcg_profile_identity, execution_context
                            ORDER BY observed_at DESC, rowid DESC) AS ordinal
                        FROM provider_connection_observations
                        WHERE profile_id COLLATE mcg_profile_identity IN (SELECT value FROM json_each($profiles))
                    ) WHERE ordinal = 1 ORDER BY execution_context
                    """;
                command.Parameters.AddWithValue("$profiles", JsonSerializer.Serialize(profileIds));
                using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    var observation = new ProviderConnectionObservation((ProviderConnectionContext)reader.GetInt32(0),
                        (ProviderConnectionSource)reader.GetInt32(1), (ProviderConnectionResult)reader.GetInt32(2),
                        (ProviderConnectionAuthMode)reader.GetInt32(3), DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture));
                    var profileId = reader.GetString(5);
                    ValidateConnectionObservation(profileId, observation);
                    result[profileId].Add(observation);
                }
            }
        }
        var historical = await ProviderConnectionHistoryReader.ReadAsync(connection, profileIds, SerializerOptions, cancellationToken, _readObserver);
        foreach (var pair in historical) result[pair.Key].AddRange(pair.Value);
        await RunNonQueryAsync(connection, "COMMIT", cancellationToken);
        return result.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<ProviderConnectionObservation>)pair.Value,
            StringComparer.OrdinalIgnoreCase);
    }
}
