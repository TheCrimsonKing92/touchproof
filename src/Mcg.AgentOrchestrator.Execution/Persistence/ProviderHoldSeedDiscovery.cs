using System.Diagnostics;
using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Microsoft.Data.Sqlite;

namespace Mcg.AgentOrchestrator.Infrastructure;

// Discovery supplies a conservative set of goal IDs. The canonical matcher still
// decides which recorded dispatch produced a refusal or recovery.
internal sealed class ProviderHoldSeedDiscovery(bool hasHistory)
{
    private readonly Dictionary<(string Goal, string Task), DateTimeOffset> _successes = [];
    private readonly Dictionary<(string Goal, string Task), string[]> _inlineProviders = [];

    internal HashSet<GoalId> FailureGoals { get; } = [];
    internal HashSet<GoalId> MarkerGoals { get; } = [];
    internal Dictionary<GoalId, ProviderHoldSeedEvidenceReader.ArchiveProjection> ArchiveEvidence { get; } = [];

    internal static async Task<ProviderHoldSeedDiscovery> ReadAsync(SqliteConnection connection,
        JsonSerializerOptions options, CancellationToken token, Action<GoalSnapshotReadObservation>? observe = null,
        bool filterTextCandidates = false, bool backfillArchiveDiscovery = false,
        Dictionary<string, long>? timings = null, bool sparseArchiveTimelines = false)
    {
        var started = Stopwatch.GetTimestamp();
        long archiveTicks = 0;
        long archiveDecodeTicks = 0;
        long documentTicks = 0;
        var hasArchive = await ProviderHoldSeedEvidenceStore.HasArchiveAsync(connection, token);
        var hasTimeline = await GoalTimelineStore.HasTableAsync(connection, token);
        var hasHistory = await GoalTaskHistoryStore.HasTableAsync(connection, token);
        // Status and Fleet request full archive timelines. Keep startup's sparse replay path.
        var storedEvidence = !sparseArchiveTimelines && hasArchive &&
            await ProviderHoldSeedEvidenceStore.HasReadableSchemaAsync(connection, token);
        // Legacy archives need a first decode. Overlap a small number of in-memory
        // decodes while retaining one ordered reader for SQLite and replay attribution.
        var archiveBatchSize = sparseArchiveTimelines ? Math.Clamp(Environment.ProcessorCount, 1, 4) : 1;
        var pending = new List<(string Id, object Payload, string ArchivedAt, bool ValidateTimeline)>(archiveBatchSize);
        var markerIndexStarted = Stopwatch.GetTimestamp();
        sparseArchiveTimelines &= hasArchive && hasTimeline;
        if (sparseArchiveTimelines && backfillArchiveDiscovery)
            await GoalTimelineStore.EnsureProviderHoldMarkerIndexAsync(connection, token);
        var markerIndexTicks = Stopwatch.GetTimestamp() - markerIndexStarted;
        var discovery = new ProviderHoldSeedDiscovery(hasHistory);
        var replayOptions = ProviderHoldSeedEvidenceReader.ArchiveReplayOptions(options);
        var remaining = new List<string>();
        var remainingTimelines = new List<string>();
        var emptyArchives = new Dictionary<string, string>(StringComparer.Ordinal);
        using var command = connection.CreateCommand();
        var indexed = hasArchive && await GoalSnapshotArchive.HasProviderHoldDiscoveryIndexAsync(connection, token);
        var hasArchiveDiscovery = !indexed && hasArchive &&
            await GoalSnapshotArchive.HasProviderHoldDiscoveryColumnAsync(connection, token);
        command.CommandText = DocumentDiscoverySql(hasArchive, hasTimeline, hasHistory, hasArchiveDiscovery, indexed,
            filterTextCandidates, sparseArchiveTimelines, backfillArchiveDiscovery, storedEvidence);
        if (sparseArchiveTimelines)
            command.Parameters.AddWithValue("$holdRules", JsonSerializer.Serialize(ProviderHoldHistory.HoldOutcomeRules));
        using (var reader = await command.ExecuteReaderAsync(token))
        {
            while (await reader.ReadAsync(token))
            {
                var id = reader.GetString(0);
                var payload = reader.GetValue(1);
                var archivedAt = reader.IsDBNull(2) ? null : reader.GetString(2);
                if (storedEvidence && reader.GetBoolean(4))
                {
                    await ProjectArchivesAsync();
                    if (ProviderHoldSeedEvidenceStore.TryDecode(id, payload, options) is { } evidence)
                    {
                        if (evidence.Timeline.Any(evt => IsMarker(evt.Message)))
                            discovery.MarkerGoals.Add(new(id));
                        discovery.ObserveArchiveProjection(id, new(evidence, null), observe);
                        continue;
                    }

                    await using var fallback = connection.CreateCommand();
                    fallback.CommandText = "SELECT snapshot_json FROM goal_snapshot_archive WHERE goal_id = $id";
                    fallback.Parameters.AddWithValue("$id", id);
                    payload = (await fallback.ExecuteScalarAsync(token))!;
                }
                observe?.Invoke(new("provider-seed-discovery", id, payload switch
                {
                    string json => json.Length,
                    byte[] bytes => bytes.Length,
                    _ => 0
                }));
                if (archivedAt is not null)
                {
                    pending.Add((id, payload, archivedAt, reader.GetBoolean(3)));
                    if (pending.Count == archiveBatchSize) await ProjectArchivesAsync();
                    continue;
                }
                await ProjectArchivesAsync();
                await ObserveDocumentAsync(id, payload);
            }
            await ProjectArchivesAsync();
        }
        var documentScanTicks = Stopwatch.GetTimestamp() - started;
        started = Stopwatch.GetTimestamp();

        command.Parameters.AddWithValue("$remaining", JsonSerializer.Serialize(remaining));
        if (hasTimeline && remainingTimelines.Count != 0)
        {
            command.CommandText = """
                SELECT DISTINCT t.goal_id FROM json_each($remainingTimelines) candidate CROSS JOIN goal_timeline t
                WHERE t.goal_id = candidate.value AND instr(t.entry_json, 'PROVIDER_BUDGET_') > 0
                    AND json_extract(t.entry_json, '$.Message') GLOB 'PROVIDER_BUDGET_*'
                """;
            command.Parameters.AddWithValue("$remainingTimelines", JsonSerializer.Serialize(remainingTimelines));
            using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token)) discovery.MarkerGoals.Add(new(reader.GetString(0)));
        }
        if (hasHistory && remaining.Count != 0)
        {
            command.CommandText = $"""
                SELECT h.goal_id, h.task_id, {VerificationSql("h.entry_json")}
                FROM json_each($remaining) candidate CROSS JOIN goal_task_history h
                WHERE h.goal_id = candidate.value AND h.kind = 'verification' AND {VerificationPredicate("h.entry_json")}
                """;
            using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
                discovery.ObserveVerification((reader.GetString(0), reader.GetString(1)),
                    JsonSerializer.Deserialize<TaskVerificationSnapshot>(reader.GetString(2), options));
        }
        var sidecarTicks = Stopwatch.GetTimestamp() - started;
        started = Stopwatch.GetTimestamp();
        if (hasArchive && backfillArchiveDiscovery)
            await GoalSnapshotArchive.BackfillProviderHoldDiscoveryAsync(connection, emptyArchives, token);
        if (timings is not null)
        {
            timings["historyDocumentReadMs"] = Milliseconds(documentScanTicks - archiveTicks - archiveDecodeTicks - documentTicks - markerIndexTicks);
            timings["historyDocumentDecodeMs"] = Milliseconds(documentTicks);
            timings["historyArchiveProjectionMs"] = Milliseconds(archiveTicks + archiveDecodeTicks);
            timings["historyArchiveDecodeWaitMs"] = Milliseconds(archiveDecodeTicks);
            timings["historySidecarReadMs"] = Milliseconds(sidecarTicks);
            timings["historyMarkerIndexMs"] = Milliseconds(markerIndexTicks);
            timings["historyDiscoveryWriteMs"] = Milliseconds(Stopwatch.GetTimestamp() - started);
        }
        return discovery;

        async Task ProjectArchivesAsync()
        {
            if (pending.Count == 0) return;
            var decodeStarted = Stopwatch.GetTimestamp();
            var decoded = pending.Count == 1
                ? [ReadArchive(pending[0].Payload)]
                : await Task.WhenAll(pending.Select(archive => Task.Run(() => ReadArchive(archive.Payload), token)));
            archiveDecodeTicks += Stopwatch.GetTimestamp() - decodeStarted;
            for (var index = 0; index < pending.Count; index++)
            {
                var (id, payload, archivedAt, validateTimeline) = pending[index];
                var archiveStarted = Stopwatch.GetTimestamp();
                var replay = decoded[index] is { } document
                    ? await discovery.TryProjectArchiveAsync(connection, id, document, replayOptions, token,
                        observe, validateTimeline)
                    : null;
                archiveTicks += Stopwatch.GetTimestamp() - archiveStarted;
                if (replay is null)
                    await ObserveDocumentAsync(id, payload);
                else if (backfillArchiveDiscovery && replay == false)
                    emptyArchives.Add(id, archivedAt);
            }
            pending.Clear();

            ProviderHoldSeedEvidenceReader.GoalFields? ReadArchive(object payload) =>
                ProviderHoldSeedEvidenceReader.TryReadArchiveDocument(payload, replayOptions, token);
        }

        async Task ObserveDocumentAsync(string id, object payload)
        {
            var documentStarted = Stopwatch.GetTimestamp();
            remaining.Add(id);
            using var stream = GoalSnapshotArchiveCodec.OpenDecodedStream(payload)!;
            var goal = await JsonSerializer.DeserializeAsync<DiscoveryGoal>(stream, options, token);
            if (goal?.Id != id) throw new InvalidDataException("Provider seed discovery goal identity mismatch.");
            if (goal.IsMetadataOnly || goal.TimelineInTable.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null))
                remainingTimelines.Add(id);
            else if (goal.Timeline?.Any(evt => IsMarker(evt.Message)) == true)
                discovery.MarkerGoals.Add(new(id));
            foreach (var task in goal.Tasks ?? [])
            {
                var key = (id, task.Id);
                foreach (var verification in (task.VerificationHistory ?? []).Append(task.LastVerification))
                    discovery.ObserveVerification(key, verification);
                var providers = (task.DispatchHistory ?? []).Append(task.LastDispatch)
                    .Select(dispatch => dispatch?.ProviderName ?? dispatch?.WorkerName).OfType<string>()
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                if (providers.Length > 0) discovery._inlineProviders[key] = providers;
            }
            documentTicks += Stopwatch.GetTimestamp() - documentStarted;
        }

        static long Milliseconds(long ticks) => (long)Stopwatch.GetElapsedTime(0, ticks).TotalMilliseconds;
    }

    internal static string DocumentDiscoverySql(bool hasArchive, bool hasTimeline, bool hasHistory,
        bool hasArchiveDiscovery, bool indexed, bool filterTextCandidates, bool sparseArchiveTimelines = false,
        bool backfillArchiveDiscovery = false, bool storedEvidence = false)
    {
        var archiveJoin = hasArchive ? "LEFT JOIN goal_snapshot_archive a ON a.goal_id = g.id" : "";
        var goalIndex = indexed ? $"INDEXED BY {GoalSnapshotArchive.ProviderHoldDiscoveryIndex}" : "";
        var goalFilter = indexed ? $"WHERE {GoalSnapshotArchive.ProviderHoldDiscoveryPredicate("g.")}" : "";
        var archiveVersion = hasArchiveDiscovery
            ? "CASE WHEN json_extract(g.snapshot_json, '$.IsMetadataOnly') = 1 THEN a.provider_hold_empty_version END"
            : "NULL";
        var archivedAt = hasArchive
            ? "CASE WHEN json_extract(g.snapshot_json, '$.IsMetadataOnly') = 1 THEN a.archived_at END"
            : "NULL";
        var document = hasArchive
            ? "CASE WHEN a.goal_id IS NOT NULL AND json_extract(g.snapshot_json, '$.IsMetadataOnly') = 1 THEN a.snapshot_json ELSE g.snapshot_json END"
            : "g.snapshot_json";
        var storedColumn = storedEvidence ? ", is_stored" : "";
        var storedJoin = "";
        if (storedEvidence)
        {
            storedJoin = $"""

                LEFT JOIN provider_hold_seed_evidence e ON e.goal_id = a.goal_id
                    AND e.format_version = {ProviderHoldSeedEvidenceStore.FormatVersion}
                    AND e.marker = {ProviderHoldSeedEvidenceStore.MarkerSql(hasArchive: true)}
                    AND json_extract(g.snapshot_json, '$.IsMetadataOnly') = 1
                """;
            document = $"CASE WHEN e.goal_id IS NOT NULL THEN e.evidence_json ELSE {document} END";
        }
        // Sidecar row presence cannot establish authority over an archived inline timeline.
        // Only a negative projection from the archive's authoritative evidence can exclude it.
        var includeUnclassifiedArchives = backfillArchiveDiscovery ? "WHEN archived_at IS NOT NULL THEN 1" : "";
        var includeStored = storedEvidence ? "WHEN is_stored THEN 1 " : "";
        var textCandidates = filterTextCandidates ? $"""
            AND CASE {includeStored}{includeUnclassifiedArchives} WHEN typeof(payload) = 'blob' THEN 1 ELSE (
                {SidecarCandidatesSql(hasTimeline, hasHistory)}
                EXISTS (SELECT 1 FROM json_each(payload, '$.Timeline') e
                    WHERE json_extract(e.value, '$.Message') GLOB 'PROVIDER_BUDGET_*') OR
                EXISTS (SELECT 1 FROM json_each(payload, '$.Tasks') t
                    WHERE {VerificationPredicate("json_extract(t.value, '$.LastVerification')")} OR
                    EXISTS (SELECT 1 FROM json_each(t.value, '$.VerificationHistory') v
                        WHERE {VerificationPredicate("v.value")}))) END
            """ : "";
        // Resolve sidecar marker and model-fit source membership in the same read as the
        // archive document. A separate candidate read could race a concurrent writer.
        var requiresTimeline = sparseArchiveTimelines ? $"""
            (id IN ({GoalTimelineStore.ProviderHoldMarkerGoalsSql}) OR id IN (
                SELECT goal_id FROM model_fit_history WHERE outcome_rule IN (SELECT value FROM json_each($holdRules))))
            """ : "1";
        // Keep hot documents sparse. Archives need the original document to distinguish
        // authoritative inline data from histories stored in the sidecar tables.
        return $"""
            WITH documents(id, payload, archived_at, empty_version{storedColumn}) AS NOT MATERIALIZED (
                SELECT g.id, {document}, {archivedAt}, {archiveVersion}{(storedEvidence ? ", e.goal_id IS NOT NULL" : "")} FROM goals g {goalIndex}
                {archiveJoin}{storedJoin}
                {goalFilter}
            )
            SELECT id, CASE WHEN archived_at IS NULL AND typeof(payload) = 'text' THEN json_object(
                'Id', json_extract(payload, '$.Id'),
                'IsMetadataOnly', json(CASE WHEN json_extract(payload, '$.IsMetadataOnly') = 1 THEN 'true' ELSE 'false' END),
                'Timeline', CASE WHEN json_extract(payload, '$.TimelineInTable') IS NULL THEN (
                    SELECT json_group_array(json_object('Message', json_extract(e.value, '$.Message')))
                    FROM json_each(payload, '$.Timeline') e
                    WHERE json_extract(e.value, '$.Message') GLOB 'PROVIDER_BUDGET_*') END,
                'TimelineInTable', json_extract(payload, '$.TimelineInTable'),
                'Tasks', (SELECT json_group_array(json_object(
                    'Id', json_extract(t.value, '$.Id'),
                    'LastVerification', json({VerificationSql("json_extract(t.value, '$.LastVerification')")}),
                    'VerificationHistory', (SELECT json_group_array(json({VerificationSql("v.value")}))
                        FROM json_each(t.value, '$.VerificationHistory') v WHERE {VerificationPredicate("v.value")}),
                    'LastDispatch', json({DispatchSql("json_extract(t.value, '$.LastDispatch')")}),
                    'DispatchHistory', (SELECT json_group_array(json({DispatchSql("d.value")}))
                        FROM json_each(t.value, '$.DispatchHistory') d)))
                    FROM json_each(payload, '$.Tasks') t)
                ) ELSE payload END, archived_at, {requiresTimeline}{storedColumn}
            FROM documents
            WHERE empty_version IS NOT {GoalSnapshotArchive.ProviderHoldEmptyVersion}
            {textCandidates}
            """;
    }

    private static string SidecarCandidatesSql(bool hasTimeline, bool hasHistory) =>
        (hasTimeline ? """
            EXISTS (SELECT 1 FROM goal_timeline t WHERE t.goal_id = documents.id
                AND instr(t.entry_json, 'PROVIDER_BUDGET_') > 0
                AND json_extract(t.entry_json, '$.Message') GLOB 'PROVIDER_BUDGET_*') OR
            """ + "\n" : "") + (hasHistory ? $"""
            EXISTS (SELECT 1 FROM goal_task_history h WHERE h.goal_id = documents.id
                AND h.kind = 'verification' AND {VerificationPredicate("h.entry_json")}) OR
            """ : "");

    private async Task<bool?> TryProjectArchiveAsync(SqliteConnection connection, string id,
        ProviderHoldSeedEvidenceReader.GoalFields document,
        JsonSerializerOptions options, CancellationToken token, Action<GoalSnapshotReadObservation>? observe,
        bool validateTimeline)
    {
        ProviderHoldSeedEvidenceReader.ArchiveProjection projection;
        try
        {
            projection = await ProviderHoldSeedEvidenceReader.ProjectDocumentAsync(id, document, options, token, connection,
                observe, () => MarkerGoals.Add(new(id)), validateTimeline);
        }
        catch (Exception exception) when (exception is InvalidDataException or JsonException or SqliteException)
        {
            // Discovery requires less than replay. A selected goal with unusable evidence
            // will still use the normal sparse and canonical fallback paths.
            return null;
        }
        return ObserveArchiveProjection(id, projection, observe);
    }

    private bool ObserveArchiveProjection(string id, ProviderHoldSeedEvidenceReader.ArchiveProjection projection,
        Action<GoalSnapshotReadObservation>? observe)
    {
        var hasReplayEvidence = MarkerGoals.Contains(new(id));
        foreach (var task in projection.Evidence.Tasks)
        {
            var key = (id, task.TaskId);
            foreach (var verification in task.VerificationHistory.Append(task.LastVerification))
                hasReplayEvidence |= ObserveVerification(key, verification);
            if (!_successes.ContainsKey(key)) continue;
            var providers = task.DispatchHistory.Append(task.LastDispatch)
                .Select(dispatch => dispatch?.ProviderName ?? dispatch?.WorkerName).OfType<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (providers.Length > 0) _inlineProviders[key] = providers;
        }
        if (hasReplayEvidence) ArchiveEvidence.Add(new(id), projection);
        observe?.Invoke(new("provider-seed-archive-projection", id));
        return hasReplayEvidence;
    }

    internal async Task<HashSet<GoalId>> FindRecoveryCandidatesAsync(SqliteConnection connection,
        ProviderHoldHistory.SeedSelection selection, CancellationToken token)
    {
        if (selection.LowerBounds.Count == 0) return [];
        var earliest = selection.LowerBounds.Values.Min();
        var found = new HashSet<GoalId>();
        var unmatched = new List<(string Goal, string Task)>();
        foreach (var (key, at) in _successes)
        {
            if (at < earliest || selection.Sources.Contains(key.Goal) || selection.Dependencies.Contains(key.Goal) ||
                found.Contains(new(key.Goal))) continue;
            if (_inlineProviders.TryGetValue(key, out var providers) && providers.Any(provider => InWindow(provider, at)))
                found.Add(new(key.Goal));
            else
                unmatched.Add(key);
        }
        if (!hasHistory || unmatched.Count == 0) return found;

        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT d.goal_id, d.task_id, coalesce(json_extract(d.entry_json, '$.ProviderName'),
                json_extract(d.entry_json, '$.WorkerName'))
            FROM json_each($tasks) task
            CROSS JOIN goal_task_history d
            WHERE d.goal_id = json_extract(task.value, '$[0]') AND d.task_id = json_extract(task.value, '$[1]')
                AND d.kind = 'dispatch'
            """;
        command.Parameters.AddWithValue("$tasks", JsonSerializer.Serialize(unmatched.Where(key =>
            !found.Contains(new(key.Goal))).Select(key => new[] { key.Goal, key.Task })));
        using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
            if (!reader.IsDBNull(2) && InWindow(reader.GetString(2), _successes[(reader.GetString(0), reader.GetString(1))]))
                found.Add(new(reader.GetString(0)));
        return found;

        bool InWindow(string provider, DateTimeOffset at) =>
            selection.LowerBounds.TryGetValue(provider, out var first) && at >= first;
    }

    private bool ObserveVerification((string Goal, string Task) key, TaskVerificationSnapshot? verification)
    {
        if (verification is { ExitCode: 0, CompletionVerdictVerifiedSuccess: true })
        {
            if (!_successes.TryGetValue(key, out var previous) || verification.CompletedAt > previous)
                _successes[key] = verification.CompletedAt;
            return true;
        }
        if (verification is not { ExitCode: not 0 }) return false;
        var goal = new GoalId(key.Goal);
        if (FailureGoals.Contains(goal)) return true;
        return IsRefusal(verification) && FailureGoals.Add(goal);
    }

    internal static bool HasReplayEvidence(GoalSnapshot goal) =>
        goal.Timeline.Any(evt => IsMarker(evt.Message)) ||
        goal.Tasks.Any(task => (task.VerificationHistory ?? []).Append(task.LastVerification)
            .Any(verification => verification is { ExitCode: 0, CompletionVerdictVerifiedSuccess: true } || IsRefusal(verification)));

    internal static bool IsRefusal(TaskVerificationSnapshot? verification) =>
        verification is { ExitCode: not 0 } && ProviderHoldEvidence.Read(TaskSpec.RestoreVerification(verification)).Confirmed;

    private static bool IsMarker(string? message) => message?.StartsWith("PROVIDER_BUDGET_", StringComparison.Ordinal) == true;

    private static string VerificationPredicate(string value) =>
        $"(json_extract({value}, '$.ExitCode') <> 0 OR json_extract({value}, '$.CompletionVerdictVerifiedSuccess') = 1)";

    // Discovery needs the complete refusal receipt, while a possible recovery only
    // needs its time and positive verdict. Canonical replay reads both in full.
    private static string VerificationSql(string value) => $"""
        CASE WHEN json_extract({value}, '$.ExitCode') <> 0 THEN {value}
             WHEN json_extract({value}, '$.CompletionVerdictVerifiedSuccess') = 1 THEN
                 json_object('ExitCode', 0, 'StandardOutput', '', 'StandardError', '',
                    'CompletionVerdictVerifiedSuccess', json('true'),
                    'CompletedAt', json_extract({value}, '$.CompletedAt'))
        END
        """;

    private static string DispatchSql(string value) => $"""
        json_object('ProviderName', coalesce(json_extract({value}, '$.ProviderName'),
            json_extract({value}, '$.WorkerName')))
        """;

    private sealed record DiscoveryGoal(string Id, DiscoveryTask[]? Tasks, Marker[]? Timeline,
        JsonElement TimelineInTable, bool IsMetadataOnly);
    private sealed record DiscoveryTask(string Id, TaskVerificationSnapshot? LastVerification,
        TaskVerificationSnapshot?[]? VerificationHistory, DispatchProvider? LastDispatch, DispatchProvider?[]? DispatchHistory);
    private sealed record DispatchProvider(string? ProviderName, string? WorkerName);
    private sealed record Marker(string? Message);
}
