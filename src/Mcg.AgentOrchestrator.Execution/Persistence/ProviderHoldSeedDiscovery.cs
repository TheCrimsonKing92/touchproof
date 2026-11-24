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
    internal Dictionary<GoalId, ProviderUsageGoalEvidence> ArchiveEvidence { get; } = [];

    internal static async Task<ProviderHoldSeedDiscovery> ReadAsync(SqliteConnection connection,
        JsonSerializerOptions options, CancellationToken token, Action<GoalSnapshotReadObservation>? observe = null,
        bool filterTextCandidates = false, bool backfillArchiveDiscovery = false)
    {
        var hasArchive = await ProviderHoldSeedEvidenceStore.HasArchiveAsync(connection, token);
        var hasTimeline = await GoalTimelineStore.HasTableAsync(connection, token);
        var hasHistory = await GoalTaskHistoryStore.HasTableAsync(connection, token);
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
            filterTextCandidates);
        using (var reader = await command.ExecuteReaderAsync(token))
        {
            while (await reader.ReadAsync(token))
            {
                var id = reader.GetString(0);
                var payload = reader.GetValue(1);
                observe?.Invoke(new("provider-seed-discovery", id, payload switch
                {
                    string json => json.Length,
                    byte[] bytes => bytes.Length,
                    _ => 0
                }));
                if (payload is byte[] && await discovery.TryProjectArchiveAsync(connection, id, payload,
                    replayOptions, token, observe) is { } hasReplayEvidence)
                {
                    if (backfillArchiveDiscovery && !hasReplayEvidence && !reader.IsDBNull(2))
                        emptyArchives.Add(id, reader.GetString(2));
                    continue;
                }
                remaining.Add(id);
                using var stream = GoalSnapshotArchiveCodec.OpenDecodedStream(payload)!;
                var goal = await JsonSerializer.DeserializeAsync<DiscoveryGoal>(stream, options, token);
                if (goal?.Id != id) throw new InvalidDataException("Provider seed discovery goal identity mismatch.");
                if (goal.IsMetadataOnly is true || goal.TimelineInTable.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null))
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
            }
        }

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
        if (hasArchive && backfillArchiveDiscovery)
            await GoalSnapshotArchive.BackfillProviderHoldDiscoveryAsync(connection, emptyArchives, token);
        return discovery;
    }

    internal static string DocumentDiscoverySql(bool hasArchive, bool hasTimeline, bool hasHistory,
        bool hasArchiveDiscovery, bool indexed, bool filterTextCandidates)
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
        // Sidecar row presence cannot establish authority over an archived inline timeline.
        // Only a negative projection from the archive's authoritative evidence can skip its blob.
        var textCandidates = filterTextCandidates ? $"""
            AND CASE WHEN typeof(payload) = 'blob' THEN 1 ELSE (
                {SidecarCandidatesSql(hasTimeline, hasHistory)}
                EXISTS (SELECT 1 FROM json_each(payload, '$.Timeline') e
                    WHERE json_extract(e.value, '$.Message') GLOB 'PROVIDER_BUDGET_*') OR
                EXISTS (SELECT 1 FROM json_each(payload, '$.Tasks') t
                    WHERE {VerificationPredicate("json_extract(t.value, '$.LastVerification')")} OR
                    EXISTS (SELECT 1 FROM json_each(t.value, '$.VerificationHistory') v
                        WHERE {VerificationPredicate("v.value")}))) END
            """ : "";
        // Project text in SQLite so large unrelated graphs never cross the managed
        // boundary. Status also checks archives that may carry inline timelines.
        return $"""
            WITH documents(id, payload, archived_at, empty_version) AS NOT MATERIALIZED (
                SELECT g.id, {document}, {archivedAt}, {archiveVersion} FROM goals g {goalIndex}
                {archiveJoin}
                {goalFilter}
            )
            SELECT id, CASE WHEN typeof(payload) = 'text' THEN json_object(
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
                ) ELSE payload END, archived_at
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

    private async Task<bool?> TryProjectArchiveAsync(SqliteConnection connection, string id, object payload,
        JsonSerializerOptions options, CancellationToken token, Action<GoalSnapshotReadObservation>? observe)
    {
        ProviderUsageGoalEvidence evidence;
        try
        {
            evidence = await ProviderHoldSeedEvidenceReader.ProjectAsync(id, payload, options, token, connection,
                observe, () => MarkerGoals.Add(new(id)));
        }
        catch (Exception exception) when (exception is InvalidDataException or JsonException or SqliteException)
        {
            // Discovery requires less than replay. A selected goal with unusable evidence
            // will still use the normal sparse and canonical fallback paths.
            return null;
        }
        var hasReplayEvidence = MarkerGoals.Contains(new(id));
        foreach (var task in evidence.Tasks)
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
        if (hasReplayEvidence) ArchiveEvidence.Add(new(id), evidence);
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

    private static bool IsRefusal(TaskVerificationSnapshot? verification) =>
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
