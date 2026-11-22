using System.Text.Json;
using System.Text.Json.Serialization;
using Mcg.AgentOrchestrator.Core;
using Microsoft.Data.Sqlite;

namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed partial class SqliteOrchestratorStateRepository
{
    private const int AttentionArchiveMarkerVersion = 1;

    public Task<AttentionReadProjection?> ReadAttentionProjectionAsync(CancellationToken cancellationToken = default) =>
        ReadAttentionProjectionAsync(new AttentionProjectionReadOptions(), cancellationToken);

    public async Task<AttentionReadProjection?> ReadAttentionProjectionAsync(AttentionProjectionReadOptions options,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await using var connection = OpenConnection();
        var hasArchive = await ProviderHoldSeedEvidenceStore.HasArchiveAsync(connection, cancellationToken);
        var hasMarkers = hasArchive && await HasAttentionArchiveMarkerSchemaAsync(connection, cancellationToken);
        var rows = new AttentionProjectionRows();
        var archiveReads = 0;
        var archiveDecodes = 0;

        await using var command = connection.CreateCommand();
        // LoadGoalsAsync's primary-key IN scan supplies the baseline goal order. Archives only
        // replace metadata stubs, exactly as GoalSnapshotArchive.HydrateAsync does.
        command.CommandText = "SELECT id, snapshot_json, source_backlog_item_id FROM goals ORDER BY id";
        if (hasMarkers)
        {
            // Goal writes advance the key with task history and timeline. The covering index supplies
            // archive metadata in the same scan snapshot without materializing the archive payload.
            command.CommandText = $"""
                SELECT g.id, g.snapshot_json, g.source_backlog_item_id,
                    CASE WHEN e.other_rows = 0 AND e.gate_rows IN (0, 1)
                              AND ($include_gate = 0 OR e.gate_rows = 0) THEN
                        (SELECT e.facts_json FROM goal_snapshot_archive a INDEXED BY attention_archive_marker_key
                         WHERE a.goal_id = g.id AND e.marker = {ProviderHoldSeedEvidenceStore.MarkerSql(hasArchive: true)})
                    END
                FROM goals g LEFT JOIN attention_archive_evidence e
                    ON e.goal_id = g.id AND e.format_version = $format
                ORDER BY g.id
                """;
            command.Parameters.AddWithValue("$include_gate", options.IncludeGateEvidence);
            command.Parameters.AddWithValue("$format", AttentionArchiveMarkerVersion);
        }
        _statementObserver?.Invoke(command.CommandText);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var hot = JsonDocument.Parse(reader.GetString(1));
            var goalId = reader.GetString(0);
            var sourceBacklogItemId = reader.IsDBNull(2) ? null : reader.GetString(2);
            var archived = hasArchive && ReadAttentionField<bool>(hot.RootElement, "IsMetadataOnly");
            if (archived && hasMarkers && ReadMarkedAttentionFacts(reader.GetValue(3), goalId, sourceBacklogItemId) is { } facts)
            {
                rows.Facts.Add(facts);
                continue;
            }

            JsonDocument? archive = null;
            try
            {
                if (archived)
                {
                    await using var readArchive = connection.CreateCommand();
                    readArchive.CommandText = "SELECT snapshot_json FROM goal_snapshot_archive WHERE goal_id = $id";
                    readArchive.Parameters.AddWithValue("$id", goalId);
                    _statementObserver?.Invoke(readArchive.CommandText);
                    var value = await readArchive.ExecuteScalarAsync(cancellationToken);
                    if (value is not null and not DBNull)
                    {
                        archiveReads++;
                        var json = GoalSnapshotArchiveCodec.Decode(value)!;
                        archiveDecodes++;
                        _readObserver?.Invoke(new("attention-archive-decode", goalId, json.Length));
                        archive = JsonDocument.Parse(json);
                    }
                }
                await rows.AddGoalAsync(connection, archive?.RootElement ?? hot.RootElement, goalId,
                    sourceBacklogItemId, options, _readObserver, cancellationToken);
            }
            finally { archive?.Dispose(); }
        }
        return new(rows.Facts, rows.Gates, rows.Questions, rows.Providers,
            new(rows.Facts.Count, archiveReads, archiveDecodes, rows.Providers.Sum(goal => goal.Tasks.Count),
                rows.Questions.Sum(question => question.TaskIds.Count)), rows.Duties, rows.EnvironmentHolds,
            rows.OperatorPreLandingGoalIds);
    }

    private sealed class AttentionProjectionRows
    {
        public List<AttentionGoalReadFacts> Facts { get; } = [];
        public List<AttentionGateEvidence> Gates { get; } = [];
        public List<AttentionPostLandingDuty> Duties { get; } = [];
        public List<string> OperatorPreLandingGoalIds { get; } = [];
        public List<AttentionOwnerQuestionEvidence> Questions { get; } = [];
        public List<AttentionEnvironmentHoldEvidence> EnvironmentHolds { get; } = [];
        public List<ProviderUsageGoalEvidence> Providers { get; } = [];

        public async Task AddGoalAsync(SqliteConnection connection, JsonElement root, string goalId,
            string? sourceBacklogItemId, AttentionProjectionReadOptions options,
            Action<GoalSnapshotReadObservation>? readObserver, CancellationToken cancellationToken)
        {
            var id = ReadAttentionField<string>(root, "Id") ?? goalId;
            var status = ReadAttentionField<GoalStatus>(root, "Status");
            var metadataOnly = ReadAttentionField<bool>(root, "IsMetadataOnly");
            var versions = metadataOnly ? [] : ReadAttentionArray<GoalBriefVersion>(root, "BriefVersions");
            var briefVersion = versions.Length == 0 ? 1 : versions.Single(version => version.IsAuthoritative).Version;
            Facts.Add(new(id, status, briefVersion, !metadataOnly && AttentionElements(root, "Tasks")
                .Any(task => ReadAttentionField<WorkTaskStatus>(task, "Status") == WorkTaskStatus.WaitingForHuman),
                sourceBacklogItemId, ReadAttentionField<string>(root, "Objective") ?? ""));
            if (metadataOnly) return; // A missing archive retains the baseline metadata-only behavior.

            var criterionEvidence = ReadAttentionArray<CriterionEvidenceObligation>(root, "CriterionEvidenceObligations");
            Duties.AddRange(criterionEvidence
                .Where(item => item.IsValidatedPostLanding &&
                    item.State is not (CriterionEvidenceState.Satisfied or CriterionEvidenceState.Repaired))
                .Select(item => new AttentionPostLandingDuty(id, item)));

            if (options.IncludeGateEvidence)
            {
                if (criterionEvidence.Any(HasOutstandingOperatorPreLandingBinding))
                    OperatorPreLandingGoalIds.Add(id);

                var obligations = new List<GateTestObligation>();
                foreach (var obligation in ReadAttentionArray<GateTestObligation>(root, "GateTestObligations"))
                {
                    var previous = obligations.FirstOrDefault(item => item.HasSameIdentity(obligation));
                    if (previous is not null)
                    {
                        if (obligation.PlannerIdentity is not null &&
                            !obligation.PlannerEvidence!.SemanticallyEquals(previous.PlannerEvidence))
                            throw new InvalidOperationException("Conflicting stored Planner evidence payload.");
                        continue;
                    }
                    obligations.Add(obligation with { Selectors = obligation.Selectors?.ToArray() ?? [] });
                }
                Gates.AddRange(obligations.Where(item => item.BriefVersion == briefVersion)
                    .Select(item => new AttentionGateEvidence(id, item)));
            }

            var hold = ReadAttentionField<GoalHoldSnapshot>(root, "CurrentHold");
            if (hold?.State == EnvironmentHold.State && status is not (GoalStatus.Completed or GoalStatus.Failed or GoalStatus.Cancelled or GoalStatus.Superseded))
                EnvironmentHolds.Add(new(id, hold));
            var needsQuestion = status is not (GoalStatus.Completed or GoalStatus.Cancelled or GoalStatus.Superseded) &&
                hold?.State is "steward-owner-question" or "author-owner-question";
            var taskIds = new List<string>();
            var tasks = new List<ProviderUsageTaskEvidence>();
            foreach (var task in AttentionElements(root, "Tasks"))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var taskId = ReadAttentionField<string>(task, "Id")!;
                if (needsQuestion) taskIds.Add(taskId);
                readObserver?.Invoke(new("attention-task-history", id));
                var history = await TaskHistoryStore.ReadAsync(connection, id, task, cancellationToken);
                var dispatches = history.Dispatch.Entries.Select(ReadAttentionDispatch).ToArray();
                var lastDispatch = history.LatestDispatch.ValueKind != JsonValueKind.Undefined
                    ? ReadAttentionDispatch(history.LatestDispatch)
                    : task.TryGetProperty("LastDispatch", out var last) && last.ValueKind != JsonValueKind.Null
                        ? ReadAttentionDispatch(last) : null;
                // Undispatched tasks matter only as the target of a reserved probe. They carry
                // no verification graph, execution output, retry state, process or worker brief.
                var taskStatus = ReadAttentionField<WorkTaskStatus>(task, "Status");
                var agentId = ReadAttentionField<string>(task, "AssignedAgentId");
                if (dispatches.Length == 0 && lastDispatch is null &&
                    (taskStatus != WorkTaskStatus.Assigned || agentId is null)) continue;
                tasks.Add(new(taskId, ReadAttentionField<AgentRole>(task, "RequiredRole"), taskStatus, agentId, lastDispatch, dispatches,
                    history.LatestVerification.ValueKind != JsonValueKind.Undefined
                        ? ReadAttentionVerification(history.LatestVerification) : ReadAttentionVerificationField(task, "LastVerification"),
                    history.Verification.Entries.Select(ReadAttentionVerification).ToArray(),
                    ReadAttentionField<DateTimeOffset?>(task, "LatestProviderBudgetRecoveryAt")));
            }
            if (needsQuestion) Questions.Add(new(id, hold!, taskIds));
            readObserver?.Invoke(new("attention-timeline", id));
            var timeline = (await new GoalTimelineStore(SerializerOptions).ReadAsync(connection, id, root, cancellationToken)).Select(evt => new ProgressEvent(
                    new GoalId(id), ReadAttentionField<string>(evt, "TaskId") is { } taskId ? new TaskId(taskId) : null,
                    ReadAttentionField<ProgressKind>(evt, "Kind"), ReadAttentionField<string>(evt, "Message")!,
                    ReadAttentionField<DateTimeOffset>(evt, "OccurredAt"),
                    OperatorIntentCreatedAt: ReadAttentionField<DateTimeOffset?>(evt, "OperatorIntentCreatedAt")))
                .Where(evt => evt.Kind is ProgressKind.TaskRedelegated or ProgressKind.ProviderHoldSubstituted or
                    ProgressKind.ProviderCapabilitySubstituted or ProgressKind.TaskCancelled ||
                    ProviderBudgetHoldSchedules.TryParseProbe(evt, out _) || ProviderBudgetHoldSchedules.TryParseReset(evt, out _) ||
                    ProviderBudgetHoldSchedules.TryParseStandaloneResult(evt, out _))
                .ToArray();
            if (tasks.Count > 0 || timeline.Length > 0) Providers.Add(new(id, status, tasks, timeline));
        }
    }

    private sealed record AttentionArchiveFacts(
        [property: JsonRequired] string GoalId,
        [property: JsonRequired] GoalStatus Status,
        [property: JsonRequired] int BriefVersion,
        [property: JsonRequired] bool HasWaitingTasks,
        [property: JsonRequired] string Title);

    private static AttentionGoalReadFacts? ReadMarkedAttentionFacts(object value, string goalId, string? sourceBacklogItemId)
    {
        if (value is not string json) return null;
        try
        {
            var facts = JsonSerializer.Deserialize<AttentionArchiveFacts>(json, SerializerOptions);
            if (facts is { HasWaitingTasks: false, Title: not null } && facts.GoalId == goalId && Enum.IsDefined(facts.Status))
                return new(goalId, facts.Status, facts.BriefVersion, false, sourceBacklogItemId, facts.Title);
        }
        catch (JsonException) { /* Malformed marker data is a cache miss. */ }
        return null;
    }

    private static async Task<bool> HasAttentionArchiveMarkerSchemaAsync(SqliteConnection connection, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT count(*) = 6 AND sum(pk) = 1 AND sum(name = 'goal_id' AND pk = 1) = 1
                   AND sum(name IN ('goal_id', 'marker', 'format_version', 'gate_rows', 'other_rows', 'facts_json')) = 6
                   AND EXISTS (SELECT 1 FROM sqlite_master WHERE type = 'index' AND tbl_name = 'goal_snapshot_archive'
                               AND name = 'attention_archive_marker_key')
            FROM pragma_table_info('attention_archive_evidence')
            """;
        try { return await command.ExecuteScalarAsync(token) is 1L; }
        catch (SqliteException) { return false; }
    }

    [StoreSchemaSource("orchestrator-state", "src/Mcg.AgentOrchestrator.Execution/Persistence/SqliteOrchestratorStateRepository.AttentionReads.cs")]
    private static Task CreateAttentionArchiveMarkerSchemaAsync(SqliteConnection connection, CancellationToken token) =>
        RunNonQueryAsync(connection, """
            CREATE TABLE IF NOT EXISTS attention_archive_evidence (
                goal_id TEXT PRIMARY KEY,
                marker TEXT NOT NULL,
                format_version INTEGER NOT NULL,
                gate_rows INTEGER NOT NULL CHECK (gate_rows IN (0, 1)),
                other_rows INTEGER NOT NULL CHECK (other_rows IN (0, 1)),
                facts_json TEXT NOT NULL
            );
            /* Cover validation with archive metadata. The archive table row can contain overflow pages. */
            CREATE INDEX IF NOT EXISTS attention_archive_marker_key
                ON goal_snapshot_archive(goal_id, archived_at, typeof(snapshot_json));
            """, token);

    internal async Task WriteAttentionArchiveEvidenceMarkersAsync(CancellationToken cancellationToken = default)
    {
        var write = await BeginWriteAsync(nameof(WriteAttentionArchiveEvidenceMarkersAsync), cancellationToken);
        await using var connection = write.Connection;
        try
        {
            if (await ProviderHoldSeedEvidenceStore.HasArchiveAsync(connection, cancellationToken))
                await WriteAttentionArchiveMarkersInTransactionAsync(connection, goalIds: null, cancellationToken);
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

    private static async Task WriteAttentionArchiveMarkersInTransactionAsync(
        SqliteConnection connection, IReadOnlyCollection<string>? goalIds, CancellationToken token)
    {
        await CreateAttentionArchiveMarkerSchemaAsync(connection, token);
        await WriteMissingAttentionMarkersAsync(connection, goalIds, token);
    }

    private static async Task WriteMissingAttentionMarkersAsync(
        SqliteConnection connection, IReadOnlyCollection<string>? goalIds, CancellationToken token)
    {
        var markers = new List<(string Key, bool GateRows, bool OtherRows, AttentionArchiveFacts Facts)>();
        var keySql = ProviderHoldSeedEvidenceStore.MarkerSql(hasArchive: true);
        var goalFilter = goalIds is null ? "" : "AND g.id IN (SELECT value FROM json_each($ids))";
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT g.id, g.snapshot_json, a.snapshot_json, {keySql}
            FROM goal_snapshot_archive a JOIN goals g ON g.id = a.goal_id
            LEFT JOIN attention_archive_evidence e
                ON e.goal_id = g.id AND e.format_version = $format AND e.marker = {keySql}
            WHERE e.goal_id IS NULL {goalFilter}
            """;
        command.Parameters.AddWithValue("$format", AttentionArchiveMarkerVersion);
        if (goalIds is not null)
            command.Parameters.AddWithValue("$ids", JsonSerializer.Serialize(goalIds, SerializerOptions));
        var options = new AttentionProjectionReadOptions(IncludeGateEvidence: true);
        await using (var reader = await command.ExecuteReaderAsync(token))
        {
            while (await reader.ReadAsync(token))
            {
                token.ThrowIfCancellationRequested();
                var goalId = reader.GetString(0);
                try
                {
                    using var hot = JsonDocument.Parse(reader.GetString(1));
                    if (!ReadAttentionField<bool>(hot.RootElement, "IsMetadataOnly") || reader.IsDBNull(2)) continue;
                    using var archive = JsonDocument.Parse(GoalSnapshotArchiveCodec.Decode(reader.GetValue(2))!);
                    var rows = new AttentionProjectionRows();
                    await rows.AddGoalAsync(connection, archive.RootElement, goalId, null, options, null, token);
                    var facts = rows.Facts.Single();
                    if (facts.GoalId != goalId) continue;
                    markers.Add((reader.GetString(3), rows.Gates.Count > 0 || rows.OperatorPreLandingGoalIds.Count > 0,
                        facts.HasWaitingTasks || rows.Duties.Count > 0 || rows.Questions.Count > 0 ||
                            rows.EnvironmentHolds.Count > 0 || rows.Providers.Count > 0,
                        new(goalId, facts.Status, facts.BriefVersion, facts.HasWaitingTasks, facts.Title)));
                }
                catch (Exception exception) when (exception is JsonException or InvalidDataException or InvalidOperationException
                    or NotSupportedException or FormatException)
                {
                    // A failed projection leaves the archive eligible for its normal read/error path.
                }
            }
        }

        if (goalIds is not null)
            command.Parameters.RemoveAt("$ids");
        command.CommandText = """
            INSERT INTO attention_archive_evidence (goal_id, marker, format_version, gate_rows, other_rows, facts_json)
            VALUES ($id, $marker, $format, $gate, $other, $facts)
            ON CONFLICT(goal_id) DO UPDATE SET marker = excluded.marker, format_version = excluded.format_version,
                gate_rows = excluded.gate_rows, other_rows = excluded.other_rows, facts_json = excluded.facts_json
            """;
        var id = command.Parameters.Add("$id", SqliteType.Text);
        var marker = command.Parameters.Add("$marker", SqliteType.Text);
        var gate = command.Parameters.Add("$gate", SqliteType.Integer);
        var other = command.Parameters.Add("$other", SqliteType.Integer);
        var json = command.Parameters.Add("$facts", SqliteType.Text);
        foreach (var row in markers)
        {
            token.ThrowIfCancellationRequested();
            id.Value = row.Facts.GoalId;
            marker.Value = row.Key;
            gate.Value = row.GateRows;
            other.Value = row.OtherRows;
            json.Value = JsonSerializer.Serialize(row.Facts, SerializerOptions);
            await command.ExecuteNonQueryAsync(token);
        }
    }

    private static bool HasOutstandingOperatorPreLandingBinding(CriterionEvidenceObligation obligation)
    {
        var candidate = string.IsNullOrWhiteSpace(obligation.ExpectedCandidateSha)
            ? obligation.CandidateSha : obligation.ExpectedCandidateSha;
        return obligation.Owner == CriterionEvidenceOwner.Operator && obligation.Phase == CriterionEvidencePhase.PreLanding &&
            obligation.State != CriterionEvidenceState.Repaired && !string.IsNullOrWhiteSpace(candidate) &&
            !obligation.HasSatisfiedEvidenceFor(candidate);
    }

    private static T? ReadAttentionField<T>(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null
            ? value.Deserialize<T>(SerializerOptions) : default;

    private static IEnumerable<JsonElement> AttentionElements(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null
            ? value.EnumerateArray() : [];

    private static T[] ReadAttentionArray<T>(JsonElement element, string name) =>
        AttentionElements(element, name).Select(value => value.Deserialize<T>(SerializerOptions)!).ToArray();

    private static TaskDispatchSnapshot ReadAttentionDispatch(JsonElement value) => new(
        ReadAttentionField<string>(value, "WorkerName")!, ReadAttentionField<string>(value, "Command")!,
        ReadAttentionField<string>(value, "WorkingDirectory")!, ReadAttentionField<DateTimeOffset>(value, "DispatchedAt"),
        ProviderName: ReadAttentionField<string>(value, "ProviderName"),
        ClaudeCredentialSourceDirectory: ReadAttentionField<string>(value, "ClaudeCredentialSourceDirectory"));

    private static TaskVerificationSnapshot? ReadAttentionVerificationField(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null
            ? ReadAttentionVerification(value) : null;

    // Structured verification fields participate in TaskSpec's history retention and same-round
    // merge rules. Keep that canonical bounded record intact rather than reimplementing the rules.
    private static TaskVerificationSnapshot ReadAttentionVerification(JsonElement value) =>
        value.Deserialize<TaskVerificationSnapshot>(SerializerOptions)!;
}
