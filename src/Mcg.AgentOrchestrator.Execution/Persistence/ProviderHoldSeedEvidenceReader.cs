using System.Text.Json;
using System.Text.Json.Serialization;
using Mcg.AgentOrchestrator.Core;
using Microsoft.Data.Sqlite;

namespace Mcg.AgentOrchestrator.Infrastructure;

/// <summary>Read-only, candidate-scoped sparse decoding. Unknown snapshot graphs are skipped
/// by the streaming serializer, including briefs, execution output and acceptance evidence.</summary>
internal static class ProviderHoldSeedEvidenceReader
{
    internal static JsonSerializerOptions ArchiveReplayOptions(JsonSerializerOptions options)
    {
        var replay = new JsonSerializerOptions(options);
        replay.Converters.Insert(0, new ArchiveVerificationConverter(options));
        return replay;
    }

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
            var evidence = await ProjectAsync(id, payload, options, cancellationToken, connection, observer);
            result.Add(evidence);
            projected?.Invoke(evidence, reader.GetString(3));
        }
        return result;
    }

    internal static async Task<ProviderUsageGoalEvidence> ProjectAsync(string id, object payload,
        JsonSerializerOptions options, CancellationToken cancellationToken = default, SqliteConnection? connection = null,
        Action<GoalSnapshotReadObservation>? observer = null)
    {
        using var stream = GoalSnapshotArchiveCodec.OpenDecodedStream(payload)!;
        var row = await GoalSnapshotByteAccounting.DeserializeAsync<GoalFields>(stream, options, cancellationToken)
            ?? throw new InvalidDataException("Missing provider seed goal evidence.");
        return await ProjectDocumentAsync(id, row, options, cancellationToken, connection, observer);
    }

    internal static GoalFields? TryReadArchiveDocument(object payload, JsonSerializerOptions options,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            using var stream = GoalSnapshotArchiveCodec.OpenDecodedStream(payload)!;
            return GoalSnapshotByteAccounting.Deserialize<GoalFields>(stream, options);
        }
        catch (Exception exception) when (exception is InvalidDataException or JsonException)
        {
            return null;
        }
    }

    internal static async Task<ProviderUsageGoalEvidence> ProjectDocumentAsync(string id, GoalFields row,
        JsonSerializerOptions options, CancellationToken cancellationToken, SqliteConnection? connection,
        Action<GoalSnapshotReadObservation>? observer, Action? foundMarker = null, bool validateArchiveTimeline = true)
    {
        if (row.Id != id) throw new InvalidDataException("Provider seed evidence goal identity mismatch.");
        var tasks = new List<ProviderUsageTaskEvidence>();
        var store = new GoalTaskHistoryStore(options);
        var fields = row.IsMetadataOnly ? [] : row.Tasks ?? [];
        var histories = await store.ReadProjectionAsync(connection, id, fields.Select(task =>
            (task.Id, task.HistoryInTable)).ToArray(), cancellationToken);
        for (var index = 0; index < fields.Count; index++)
        {
            var task = fields[index];
            var history = histories[index];
            tasks.Add(new ProviderUsageTaskEvidence(
                task.Id, task.RequiredRole, task.Status, task.AssignedAgentId,
                history is { LatestDispatchInTable: true }
                    ? ProjectDispatch(history.Dispatches[^1]) : task.LastDispatch?.Snapshot(),
                history is null ? (task.DispatchHistory ?? []).Select(dispatch => dispatch.Snapshot()).ToArray()
                    : history.Dispatches.Select(ProjectDispatch).ToArray(),
                history is { LatestVerificationInTable: true }
                    ? history.Verifications[^1] : task.LastVerification,
                history?.Verifications ?? task.VerificationHistory ?? [],
                task.LatestProviderBudgetRecoveryAt) { LastProcess = task.LastProcess });
        }
        var timeline = row.IsMetadataOnly ? [] : row.TimelineInTable.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
            ? row.Timeline ?? [] : SkipSidecarTimeline() ? [] : (await new GoalTimelineStore(options).ReadAsync(
                connection, id, default, row.TimelineInTable, cancellationToken, TimelineSelectionSql,
                observer is null ? null : size => observer(new("provider-seed-timeline-entry", id, size))))
                .Select(entry => entry.Deserialize<EventFields>(options)!).ToArray();
        if (foundMarker is not null && timeline.Any(evt => evt.Message?.StartsWith("PROVIDER_BUDGET_", StringComparison.Ordinal) == true))
            foundMarker();
        return new(id, row.Status, tasks,
            timeline.Select(evt => new ProgressEvent(new GoalId(id),
                evt.TaskId is { } taskId ? new TaskId(taskId) : null, evt.Kind, evt.Message, evt.OccurredAt,
                OperatorIntentCreatedAt: evt.OperatorIntentCreatedAt)).Where(Relevant).ToArray());

        // A transition can affect a probe only in the timeline that owns its marker.
        // Refusals still require canonical timeline validation before startup is ready.
        bool SkipSidecarTimeline() => !validateArchiveTimeline &&
            row.TimelineInTable.ValueKind == JsonValueKind.Number && row.TimelineInTable.TryGetInt32(out var count) &&
            count >= 0 && !tasks.Any(task => task.VerificationHistory.Append(task.LastVerification)
                .Any(ProviderHoldSeedDiscovery.IsRefusal));
    }

    private static TaskDispatchSnapshot ProjectDispatch(TaskDispatchSnapshot dispatch) =>
        new(dispatch.WorkerName, dispatch.Command, dispatch.WorkingDirectory, dispatch.DispatchedAt,
            ProviderName: dispatch.ProviderName, ModelName: dispatch.ModelName,
            ClaudeCredentialSourceDirectory: dispatch.ClaudeCredentialSourceDirectory,
            ClaudeApiHoldIdentity: dispatch.ClaudeApiHoldIdentity);

    private static readonly ProgressKind[] ReplayTransitions =
        [ProgressKind.TaskRedelegated, ProgressKind.ProviderHoldSubstituted, ProgressKind.ProviderCapabilitySubstituted, ProgressKind.TaskCancelled];

    private static readonly string TimelineSelectionSql = $"""
        json_extract(entry_json, '$.Message') GLOB 'PROVIDER_BUDGET_*'
        OR trim(json_extract(entry_json, '$.Kind')) COLLATE NOCASE IN ({string.Join(", ",
            ReplayTransitions.SelectMany(kind => new[] { $"'{kind}'", $"'{(int)kind}'" }))})
        """;

    private static bool Relevant(ProgressEvent evt) =>
        ReplayTransitions.Contains(evt.Kind) ||
        ProviderBudgetHoldSchedules.TryParseProbe(evt, out _) || ProviderBudgetHoldSchedules.TryParseReset(evt, out _) ||
        ProviderBudgetHoldSchedules.TryParseStandaloneResult(evt, out _);

    internal sealed record GoalFields(string Id, GoalStatus Status, bool IsMetadataOnly,
        IReadOnlyList<TaskFields>? Tasks, IReadOnlyList<EventFields>? Timeline, JsonElement TimelineInTable);

    internal sealed record EventFields(string? TaskId, ProgressKind Kind, string Message,
        DateTimeOffset OccurredAt, DateTimeOffset? OperatorIntentCreatedAt);

    // Verification records remain intact: TaskSpec owns bounded history retention and same-round
    // enrichment. Stripping their identity/enrichment fields could merge different observations.
    internal sealed record TaskFields(string Id, AgentRole RequiredRole, WorkTaskStatus Status, string? AssignedAgentId,
        DispatchFields? LastDispatch,
        IReadOnlyList<DispatchFields>? DispatchHistory,
        TaskVerificationSnapshot? LastVerification,
        IReadOnlyList<TaskVerificationSnapshot>? VerificationHistory,
        JsonElement HistoryInTable,
        DateTimeOffset? LatestProviderBudgetRecoveryAt, TaskProcessSnapshot? LastProcess);

    internal sealed record DispatchFields(string WorkerName, string Command, string WorkingDirectory,
        DateTimeOffset DispatchedAt, string? ProviderName, string? ClaudeCredentialSourceDirectory,
        string? ModelName, string? ClaudeApiHoldIdentity)
    {
        internal TaskDispatchSnapshot Snapshot() => new(WorkerName, Command, WorkingDirectory, DispatchedAt,
            ProviderName: ProviderName, ModelName: ModelName, ClaudeCredentialSourceDirectory: ClaudeCredentialSourceDirectory,
            ClaudeApiHoldIdentity: ClaudeApiHoldIdentity);
    }

    // Availability replay uses the verdict and attempt identity. A zero-exit receipt
    // cannot be a refusal, and authoritative text is outside its same-round identity.
    private sealed class ArchiveVerificationConverter(JsonSerializerOptions options) : JsonConverter<TaskVerificationSnapshot>
    {
        public override TaskVerificationSnapshot? Read(ref Utf8JsonReader reader, Type typeToConvert,
            JsonSerializerOptions serializerOptions)
        {
            var probe = reader;
            while (probe.Read() && probe.TokenType == JsonTokenType.PropertyName)
            {
                var isExitCode = probe.ValueTextEquals(nameof(TaskVerificationSnapshot.ExitCode));
                if (!probe.Read()) break;
                if (isExitCode && probe.TokenType == JsonTokenType.Number &&
                    probe.TryGetInt32(out var exitCode) && exitCode == 0)
                {
                    var compact = reader;
                    var verification = JsonSerializer.Deserialize<AvailabilityFields>(ref compact, options);
                    if (verification is { ExitCode: 0 })
                    {
                        reader = compact;
                        return verification.Snapshot();
                    }
                    break;
                }
                if (isExitCode || !probe.TrySkip()) break;
            }
            return JsonSerializer.Deserialize<TaskVerificationSnapshot>(ref reader, options);
        }

        public override void Write(Utf8JsonWriter writer, TaskVerificationSnapshot value,
            JsonSerializerOptions serializerOptions) => JsonSerializer.Serialize(writer, value, options);
    }

    // Keep the round identity, dispatch match, and verdict of a zero-exit receipt.
    // Finding-evidence receipts also protect the round when TaskSpec caps its history.
    private sealed record AvailabilityFields(
        string Command, string WorkingDirectory, int ExitCode, string StandardOutput, string StandardError,
        DateTimeOffset CompletedAt, string? StandardOutputPath, string? StandardErrorPath,
        ProviderFailureKind ProviderFailureKind, string? ReviewedCommit, bool WorkerResultPresent,
        DateTimeOffset? DispatchStartedAt, int? ChildProcessId, int? ChildExitCode,
        IReadOnlyList<FindingEvidenceReceipt>? FindingEvidenceReceipts,
        bool CompletionVerdictVerifiedSuccess, string? CompletionVerdictRule,
        VerificationEvidenceSource EvidenceSource, DateTimeOffset? DispatchAttemptAt, string? OrchestratorFailureReason)
    {
        internal TaskVerificationSnapshot Snapshot() =>
            new(Command, WorkingDirectory, ExitCode, StandardOutput, StandardError, CompletedAt,
                StandardOutputPath: StandardOutputPath, StandardErrorPath: StandardErrorPath,
                ProviderFailureKind: ProviderFailureKind, ReviewedCommit: ReviewedCommit, WorkerResultPresent: WorkerResultPresent,
                DispatchStartedAt: DispatchStartedAt, ChildProcessId: ChildProcessId, ChildExitCode: ChildExitCode,
                FindingEvidenceReceipts: FindingEvidenceReceipts,
                CompletionVerdictVerifiedSuccess: CompletionVerdictVerifiedSuccess, CompletionVerdictRule: CompletionVerdictRule,
                EvidenceSource: EvidenceSource, DispatchAttemptAt: DispatchAttemptAt, OrchestratorFailureReason: OrchestratorFailureReason);
    }
}
