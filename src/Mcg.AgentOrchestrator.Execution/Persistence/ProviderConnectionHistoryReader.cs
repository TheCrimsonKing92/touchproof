using System.Text.Json;
using System.Text.Json.Serialization;
using Mcg.AgentOrchestrator.Core;
using Microsoft.Data.Sqlite;

namespace Mcg.AgentOrchestrator.Infrastructure;

/// <summary>Sparse status/attribution projection. No command text, output, account identity or
/// credential path is materialized. Archive decoding is streaming; hot documents project in SQL.</summary>
internal static class ProviderConnectionHistoryReader
{
    private static readonly string[] VerificationFields = ["CompletedAt", "ExitCode", "ProviderFailureKind",
        "DispatchAttemptAt", "EvidenceSource", "WorkerResultPresent"];
    private static readonly string[] DispatchFields = ["WorkerName", "DispatchedAt", "SandboxLowIntegrity"];

    internal static async Task<IReadOnlyDictionary<string, IReadOnlyList<ProviderConnectionObservation>>> ReadAsync(SqliteConnection connection,
        IReadOnlyList<string> profileIds, JsonSerializerOptions options, CancellationToken token,
        Action<GoalSnapshotReadObservation>? observer = null)
    {
        var profiles = profileIds.ToDictionary(id => LunaLaneNames.NormalizeProfileName(id)
            ?? throw new InvalidDataException("Provider connection profile identity is missing."), id => id, StringComparer.OrdinalIgnoreCase);
        var archive = await ProviderHoldSeedEvidenceStore.HasArchiveAsync(connection, token);
        using var command = connection.CreateCommand();
        var metadataOnly = "json_extract(g.snapshot_json, '$.IsMetadataOnly') = 1";
        command.CommandText = $"""
            SELECT g.id, CASE WHEN {metadataOnly} THEN NULL ELSE {HotProjection()} END
                { (archive ? ", CASE WHEN " + metadataOnly + " THEN a.snapshot_json END" : ", NULL") }
            FROM goals g {(archive ? "LEFT JOIN goal_snapshot_archive a ON a.goal_id = g.id" : "")}
            """;
        var observations = profileIds.ToDictionary(id => id, _ => new List<ProviderConnectionObservation>(), StringComparer.OrdinalIgnoreCase);
        using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            var id = reader.GetString(0);
            if (reader.IsDBNull(1) && reader.IsDBNull(2))
                throw new InvalidDataException("Provider connection history archive is unavailable.");
            var payload = reader.IsDBNull(1) ? reader.GetValue(2) : reader.GetValue(1);
            observer?.Invoke(new("provider-connection-history-projection", id));
            using var stream = GoalSnapshotArchiveCodec.OpenDecodedStream(payload)!;
            var goal = await JsonSerializer.DeserializeAsync<GoalFields>(stream, options, token)
                ?? throw new InvalidDataException("Provider connection history is unavailable.");
            if (goal.Id != id || goal.IsMetadataOnly)
                throw new InvalidDataException("Provider connection history identity is invalid.");
            foreach (var task in goal.Tasks ?? [])
            {
                var history = await new GoalTaskHistoryStore(options).ReadMetadataAsync<Verification, Dispatch>(connection,
                    id, task.Id, JsonSerializer.SerializeToElement(task.VerificationHistory ?? [], options),
                    JsonSerializer.SerializeToElement(task.DispatchHistory ?? [], options), task.HistoryInTable,
                    ProjectVerification, ProjectDispatch, token);
                var verifications = history.Verification.Entries.Select(e => e.Deserialize<Verification>(options)!)
                    .Append(history.LatestVerification.ValueKind != JsonValueKind.Undefined
                        ? history.LatestVerification.Deserialize<Verification>(options) : task.LastVerification)
                    .Where(v => v is not null).Cast<Verification>().Distinct();
                var dispatches = history.Dispatch.Entries.Select(e => e.Deserialize<Dispatch>(options)!)
                    .Append(history.LatestDispatch.ValueKind != JsonValueKind.Undefined
                        ? history.LatestDispatch.Deserialize<Dispatch>(options) : task.LastDispatch)
                    .Where(d => d is not null).Cast<Dispatch>().Distinct().ToArray();
                foreach (var verification in verifications)
                {
                    if (verification.CompletedAt is not { } completed || completed == default || verification.DispatchAttemptAt is not { } attempt ||
                        verification.EvidenceSource == VerificationEvidenceSource.Manual) continue;
                    // Timestamp is the persisted attempt identity. Never zip lists or pair with an
                    // unrelated LastDispatch. Conflicting attribution is unknown, not a guess.
                    var matches = dispatches.Where(d => d.DispatchedAt == attempt).ToArray();
                    if (matches.Length != 1 || attempt > completed) continue;
                    var dispatch = matches[0];
                    if (string.IsNullOrWhiteSpace(dispatch.WorkerName) ||
                        !profiles.TryGetValue(LunaLaneNames.NormalizeProfileName(dispatch.WorkerName) ?? "", out var profileId)) continue;
                    var outcome = verification switch
                    {
                        { ProviderFailureKind: ProviderFailureKind.AuthenticationRequired } => ProviderConnectionResult.AuthenticationFailed,
                        { HasOrchestratorFailure: true } or { ExitCode: not null and not 0 } => ProviderConnectionResult.WorkerFailed,
                        { ExitCode: 0, WorkerResultPresent: true } => ProviderConnectionResult.WorkerSucceeded,
                        _ => ProviderConnectionResult.Unknown
                    };
                    observations[profileId].Add(new(dispatch.SandboxLowIntegrity == true ? ProviderConnectionContext.WorkerSandbox
                            : ProviderConnectionContext.WorkerUnspecified,
                        ProviderConnectionSource.RecordedInvocation, outcome, ObservedAt: completed.ToUniversalTime(),
                        IsHistorical: true));
                }
            }
        }
        return observations.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<ProviderConnectionObservation>)pair.Value
            .GroupBy(o => o.ExecutionContext).Select(group =>
            {
                var latest = group.Max(o => o.ObservedAt);
                var candidates = group.Where(o => o.ObservedAt == latest).Distinct().ToArray();
                return candidates.Length == 1 ? candidates[0] : candidates[0] with { Result = ProviderConnectionResult.Unknown };
            }).OrderBy(o => o.ExecutionContext).ToArray(), StringComparer.OrdinalIgnoreCase);
    }

    private static string HotProjection()
    {
        string Entry(string expression, bool verification) => verification ? ProjectVerification(expression) : ProjectDispatch(expression);
        string Latest(string property, bool verification) =>
            $"CASE WHEN json_type(task.value, '$.{property}') = 'object' THEN {Entry("json_extract(task.value, '$." + property + "')", verification)} ELSE NULL END";
        string History(string property, bool verification) =>
            $"json((SELECT json_group_array(json({Entry("entry.value", verification)})) FROM json_each(task.value, '$.{property}') entry))";
        return $"""
            json_object('Id', g.id, 'Tasks', json((SELECT json_group_array(json_object(
                'Id', json_extract(task.value, '$.Id'), 'HistoryInTable', json_extract(task.value, '$.HistoryInTable'),
                'LastVerification', json({Latest("LastVerification", true)}),
                'LastDispatch', json({Latest("LastDispatch", false)}),
                'VerificationHistory', {History("VerificationHistory", true)},
                'DispatchHistory', {History("DispatchHistory", false)}))
                FROM json_each(g.snapshot_json, '$.Tasks') task)))
            """;
    }

    private static string ProjectVerification(string expression)
    {
        // SQLite trim defaults to spaces. Match .NET's whitespace set used by verification.Succeeded.
        const string whitespace = "char(9,10,11,12,13,32,133,160,5760,8192,8193,8194,8195,8196,8197,8198,8199,8200,8201,8202,8232,8233,8239,8287,12288)";
        return $"""
            json_set({GoalTaskHistoryStore.MetadataSql(expression, VerificationFields)}, '$.HasOrchestratorFailure',
                json(CASE WHEN trim(json_extract(({expression}), '$.OrchestratorFailureReason'), {whitespace}) <> ''
                    THEN 'true' ELSE 'false' END))
            """;
    }

    private static string ProjectDispatch(string expression) => GoalTaskHistoryStore.MetadataSql(expression, DispatchFields);

    private sealed record GoalFields(string Id, bool IsMetadataOnly, IReadOnlyList<TaskFields>? Tasks);
    private sealed record TaskFields(string Id, Verification? LastVerification, Dispatch? LastDispatch,
        IReadOnlyList<Verification>? VerificationHistory, IReadOnlyList<Dispatch>? DispatchHistory, JsonElement HistoryInTable);
    private sealed record Verification(DateTimeOffset? CompletedAt, int? ExitCode, ProviderFailureKind? ProviderFailureKind,
        DateTimeOffset? DispatchAttemptAt, VerificationEvidenceSource? EvidenceSource, bool? WorkerResultPresent,
        bool HasOrchestratorFailure)
    {
        // Archived snapshots stream the original JSON. Keep only the derived flag in the record.
        [JsonPropertyName("OrchestratorFailureReason")]
        public string? ArchivedFailureReason { init => HasOrchestratorFailure = !string.IsNullOrWhiteSpace(value); }
    }
    private sealed record Dispatch(string WorkerName, DateTimeOffset? DispatchedAt, bool? SandboxLowIntegrity);
}
