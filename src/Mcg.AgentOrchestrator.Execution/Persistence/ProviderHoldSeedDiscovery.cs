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

    internal static async Task<ProviderHoldSeedDiscovery> ReadAsync(SqliteConnection connection,
        JsonSerializerOptions options, CancellationToken token, Action<GoalSnapshotReadObservation>? observe = null)
    {
        var hasArchive = await ProviderHoldSeedEvidenceStore.HasArchiveAsync(connection, token);
        var hasTimeline = await GoalTimelineStore.HasTableAsync(connection, token);
        var hasHistory = await GoalTaskHistoryStore.HasTableAsync(connection, token);
        var discovery = new ProviderHoldSeedDiscovery(hasHistory);
        using var command = connection.CreateCommand();
        var document = hasArchive
            ? "CASE WHEN a.goal_id IS NOT NULL AND json_extract(g.snapshot_json, '$.IsMetadataOnly') = 1 THEN a.snapshot_json ELSE g.snapshot_json END"
            : "g.snapshot_json";
        // Project text in SQLite so large unrelated graphs never cross the managed
        // boundary. Compressed legacy archives have to be decoded once here.
        command.CommandText = $"""
            WITH documents(id, payload) AS NOT MATERIALIZED (
                SELECT g.id, {document} FROM goals g
                {(hasArchive ? "LEFT JOIN goal_snapshot_archive a ON a.goal_id = g.id" : "")}
            )
            SELECT id, CASE WHEN typeof(payload) = 'text' THEN json_object(
                'Id', json_extract(payload, '$.Id'),
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
                ) ELSE payload END
            FROM documents
            """;
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
                using var stream = GoalSnapshotArchiveCodec.OpenDecodedStream(payload)!;
                var goal = await JsonSerializer.DeserializeAsync<DiscoveryGoal>(stream, options, token);
                if (goal?.Id != id) throw new InvalidDataException("Provider seed discovery goal identity mismatch.");
                if (goal.TimelineInTable.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null &&
                    goal.Timeline?.Any(evt => evt.Message?.StartsWith("PROVIDER_BUDGET_", StringComparison.Ordinal) == true) == true)
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

        if (hasTimeline)
        {
            command.CommandText = """
                SELECT DISTINCT t.goal_id FROM goal_timeline t JOIN goals g ON g.id = t.goal_id
                WHERE instr(t.entry_json, 'PROVIDER_BUDGET_') > 0
                    AND json_extract(t.entry_json, '$.Message') GLOB 'PROVIDER_BUDGET_*'
                """;
            using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token)) discovery.MarkerGoals.Add(new(reader.GetString(0)));
        }
        if (hasHistory)
        {
            command.CommandText = $"""
                SELECT h.goal_id, h.task_id, {VerificationSql("h.entry_json")}
                FROM goal_task_history h JOIN goals g ON g.id = h.goal_id
                WHERE h.kind = 'verification' AND {VerificationPredicate("h.entry_json")}
                """;
            using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
                discovery.ObserveVerification((reader.GetString(0), reader.GetString(1)),
                    JsonSerializer.Deserialize<TaskVerificationSnapshot>(reader.GetString(2), options));
        }
        return discovery;
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

    private void ObserveVerification((string Goal, string Task) key, TaskVerificationSnapshot? verification)
    {
        if (verification is { ExitCode: 0, CompletionVerdictVerifiedSuccess: true } &&
            (!_successes.TryGetValue(key, out var previous) || verification.CompletedAt > previous))
            _successes[key] = verification.CompletedAt;
        if (verification is { ExitCode: not 0 } && !FailureGoals.Contains(new(key.Goal)) &&
            ProviderHoldEvidence.Read(TaskSpec.RestoreVerification(verification)).Confirmed)
            FailureGoals.Add(new(key.Goal));
    }

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

    private sealed record DiscoveryGoal(string Id, DiscoveryTask[]? Tasks, Marker[]? Timeline, JsonElement TimelineInTable);
    private sealed record DiscoveryTask(string Id, TaskVerificationSnapshot? LastVerification,
        TaskVerificationSnapshot?[]? VerificationHistory, DispatchProvider? LastDispatch, DispatchProvider?[]? DispatchHistory);
    private sealed record DispatchProvider(string? ProviderName, string? WorkerName);
    private sealed record Marker(string? Message);
}
