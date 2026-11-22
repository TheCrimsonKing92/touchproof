using System.Text.Json;
using System.Text.Json.Serialization;
using Mcg.AgentOrchestrator.Core;
using Microsoft.Data.Sqlite;

namespace Mcg.AgentOrchestrator.Infrastructure;

/// <summary>Read-only, candidate-scoped sparse decoding. Unknown snapshot graphs are skipped
/// by the streaming serializer, including briefs, execution output and acceptance evidence.</summary>
internal static class ProviderHoldSeedEvidenceReader
{
    internal static async Task<IReadOnlyList<ProviderUsageGoalEvidence>> ReadAsync(SqliteConnection connection,
        IReadOnlyCollection<GoalId> ids, JsonSerializerOptions options, CancellationToken cancellationToken)
        => await ReadSnapshotsAsync(connection, ids, options, cancellationToken);

    internal static async Task<IReadOnlyList<ProviderUsageGoalEvidence>> ReadSnapshotsAsync(SqliteConnection connection,
        IReadOnlyCollection<GoalId> ids, JsonSerializerOptions options, CancellationToken cancellationToken,
        Action<GoalSnapshotReadObservation>? observer = null,
        Action<ProviderUsageGoalEvidence, string>? projected = null)
    {
        if (ids.Count == 0) return [];
        var hasArchive = await ProviderHoldSeedEvidenceStore.HasArchiveAsync(connection, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = hasArchive ? $"""
            SELECT g.id, g.snapshot_json,
                CASE WHEN json_extract(g.snapshot_json, '$.IsMetadataOnly') = 1 THEN a.snapshot_json END,
                {ProviderHoldSeedEvidenceStore.MarkerSql(hasArchive)}
            FROM goals g LEFT JOIN goal_snapshot_archive a ON a.goal_id = g.id
            WHERE g.id IN (SELECT value FROM json_each($ids)) ORDER BY g.id
            """ : $"""
            SELECT g.id, g.snapshot_json, NULL, {ProviderHoldSeedEvidenceStore.MarkerSql(hasArchive)} FROM goals g
            WHERE g.id IN (SELECT value FROM json_each($ids)) ORDER BY g.id
            """;
        command.Parameters.AddWithValue("$ids", JsonSerializer.Serialize(ids.Select(id => id.Value)));
        var result = new List<ProviderUsageGoalEvidence>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var payload = reader.IsDBNull(2) ? reader.GetValue(1) : reader.GetValue(2);
            var id = reader.GetString(0);
            observer?.Invoke(new("provider-seed-snapshot-decode", id));
            var evidence = await ProjectAsync(id, payload, options, cancellationToken, connection);
            result.Add(evidence);
            projected?.Invoke(evidence, reader.GetString(3));
        }
        return result;
    }

    internal static async Task<ProviderUsageGoalEvidence> ProjectAsync(string id, object payload,
        JsonSerializerOptions options, CancellationToken cancellationToken = default, SqliteConnection? connection = null)
    {
        using var stream = GoalSnapshotArchiveCodec.OpenDecodedStream(payload)!;
        var row = await GoalSnapshotByteAccounting.DeserializeAsync<GoalFields>(stream, options, cancellationToken)
            ?? throw new InvalidDataException("Missing provider seed goal evidence.");
        if (row.Id != id) throw new InvalidDataException("Provider seed evidence goal identity mismatch.");
        var tasks = new List<ProviderUsageTaskEvidence>();
        var store = new GoalTaskHistoryStore(options);
        foreach (var task in row.IsMetadataOnly ? [] : row.Tasks ?? [])
        {
            var history = await store.ReadAsync(connection, id, task.Id, task.VerificationEntries,
                task.DispatchEntries, task.HistoryInTable, cancellationToken);
            tasks.Add(new ProviderUsageTaskEvidence(
                task.Id, task.RequiredRole, task.Status, task.AssignedAgentId,
                history.LatestDispatch.ValueKind != JsonValueKind.Undefined
                    ? history.LatestDispatch.Deserialize<DispatchFields>(options)!.Snapshot() : task.LastDispatch?.Snapshot(),
                history.Dispatch.Entries.Select(dispatch => dispatch.Deserialize<DispatchFields>(options)!.Snapshot()).ToArray(),
                history.LatestVerification.ValueKind != JsonValueKind.Undefined
                    ? history.LatestVerification.Deserialize<TaskVerificationSnapshot>(options) : task.LastVerification,
                history.Verification.Entries.Select(entry => entry.Deserialize<TaskVerificationSnapshot>(options)!).ToArray(),
                task.LatestProviderBudgetRecoveryAt));
        }
        var timeline = row.IsMetadataOnly ? [] : row.TimelineInTable.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
            ? row.Timeline ?? [] : (await new GoalTimelineStore(options).ReadAsync(
                connection, id, default, row.TimelineInTable, cancellationToken))
                .Select(entry => entry.Deserialize<EventFields>(options)!).ToArray();
        return new(id, row.Status, tasks,
            timeline.Select(evt => new ProgressEvent(new GoalId(id),
                evt.TaskId is { } taskId ? new TaskId(taskId) : null, evt.Kind, evt.Message, evt.OccurredAt,
                OperatorIntentCreatedAt: evt.OperatorIntentCreatedAt)).Where(Relevant).ToArray());
    }

    private static bool Relevant(ProgressEvent evt) =>
        evt.Kind is ProgressKind.TaskRedelegated or ProgressKind.ProviderHoldSubstituted or
            ProgressKind.ProviderCapabilitySubstituted or ProgressKind.TaskCancelled ||
        ProviderBudgetHoldSchedules.TryParseProbe(evt, out _) || ProviderBudgetHoldSchedules.TryParseReset(evt, out _) ||
        ProviderBudgetHoldSchedules.TryParseStandaloneResult(evt, out _);

    private sealed record GoalFields(string Id, GoalStatus Status, bool IsMetadataOnly,
        IReadOnlyList<TaskFields>? Tasks, IReadOnlyList<EventFields>? Timeline, JsonElement TimelineInTable);

    private sealed record EventFields(string? TaskId, ProgressKind Kind, string Message,
        DateTimeOffset OccurredAt, DateTimeOffset? OperatorIntentCreatedAt);

    // Verification records remain intact: TaskSpec owns bounded history retention and same-round
    // enrichment. Stripping their identity/enrichment fields could merge different observations.
    private sealed record TaskFields(string Id, AgentRole RequiredRole, WorkTaskStatus Status, string? AssignedAgentId,
        DispatchFields? LastDispatch,
        [property: JsonPropertyName(GoalTaskHistoryStore.DispatchProperty)] JsonElement DispatchEntries,
        TaskVerificationSnapshot? LastVerification,
        [property: JsonPropertyName(GoalTaskHistoryStore.VerificationProperty)] JsonElement VerificationEntries,
        JsonElement HistoryInTable,
        DateTimeOffset? LatestProviderBudgetRecoveryAt);

    private sealed record DispatchFields(string WorkerName, string Command, string WorkingDirectory,
        DateTimeOffset DispatchedAt, string? ProviderName, string? ClaudeCredentialSourceDirectory)
    {
        internal TaskDispatchSnapshot Snapshot() => new(WorkerName, Command, WorkingDirectory, DispatchedAt,
            ProviderName: ProviderName, ClaudeCredentialSourceDirectory: ClaudeCredentialSourceDirectory);
    }
}
