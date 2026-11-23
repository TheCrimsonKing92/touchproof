using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Microsoft.Data.Sqlite;

namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed partial class SqliteOrchestratorStateRepository
{
    private async Task<GoalSnapshot> ProjectTickGoalAsync(SqliteConnection connection, GoalSnapshot snapshot,
        GoalTaskHistoryStore.DocumentMarkers markers, long version, CancellationToken token)
    {
        var id = snapshot.Id;
        if (snapshot.IsMetadataOnly)
        {
            var (archived, utf8Bytes) = await GoalSnapshotArchive.ReadDocumentAsync(connection, id, token);
            if (archived is null) return snapshot;
            _readObserver?.Invoke(new("archive-attempt", id, archived.Length));
            markers = new GoalTaskHistoryStore.DocumentMarkers();
            snapshot = TaskHistoryStore.DecodeDocument(archived, markers, utf8Bytes)
                ?? throw new JsonException($"Archived snapshot for goal '{id}' is null.");
            _readObserver?.Invoke(new("archive-deserialized", id, archived.Length));
            if (snapshot.IsMetadataOnly) return snapshot;
        }

        var timelineStore = new GoalTimelineStore(SerializerOptions);
        var (count, first) = await timelineStore.ReadTickSummaryAsync(connection, snapshot, markers.Timeline, token);
        var projected = await TaskHistoryStore.ProjectTickTasksAsync(connection, snapshot, markers,
            taskId => ReadTickRowsAsync(id, version, async (conn, root, _) =>
            {
                var task = root.GetProperty("Tasks").EnumerateArray().FirstOrDefault(candidate =>
                    candidate.GetProperty("Id").GetString() == taskId);
                if (task.ValueKind == JsonValueKind.Undefined)
                    return new TaskHistoryRows([], []);
                var rows = await TaskHistoryStore.ReadAsync(conn, id, task);
                return new TaskHistoryRows(
                    IsNullHistory(rows.Verification, task, "VerificationHistory") ? null : rows.Verification.Entries
                        .Select(entry => entry.Deserialize<TaskVerificationSnapshot>(SerializerOptions)!).ToArray(),
                    IsNullHistory(rows.Dispatch, task, "DispatchHistory") ? null : rows.Dispatch.Entries
                        .Select(entry => entry.Deserialize<TaskDispatchSnapshot>(SerializerOptions)!).ToArray());
            }).GetAwaiter().GetResult(), token);

        return projected with
        {
            Timeline = [],
            TickRowVersion = version,
            DeferredTimeline = new(count, first, () => ReadTickRowsAsync(id, version, async (conn, root, rowVersion) =>
            {
                var entries = await timelineStore.ReadAsync(conn, id, root);
                return new GoalTimelineRows(rowVersion,
                    entries.Select(entry => entry.Deserialize<ProgressEventSnapshot>(SerializerOptions)!).ToArray());
            }).GetAwaiter().GetResult())
        };
    }

    private static bool IsNullHistory(GoalTaskHistoryStore.HistoryEntries rows, JsonElement task, string property) =>
        rows.FromTable ? rows.IsNull : !task.TryGetProperty(property, out var inline) || inline.ValueKind == JsonValueKind.Null;

    private Task<T> ReadTickRowsAsync<T>(string goalId, long minimumVersion,
        Func<SqliteConnection, JsonElement, long, Task<T>> read) => WithBusyRetryAsync(async () =>
    {
        await using var connection = StateDbConnectionFactory.Open(
            _dbPath, StateDbConnectionProfile.QueryOnlyRead, _writeTelemetry.Options.BusyTimeoutMilliseconds, _statementObserver);
        using var transaction = connection.BeginTransaction(deferred: true);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT snapshot_json, COALESCE(version, 0) FROM goals WHERE id = $id";
        command.Parameters.AddWithValue("$id", goalId);
        string json;
        long version;
        using (var reader = await command.ExecuteReaderAsync())
        {
            if (!await reader.ReadAsync() || reader.GetInt64(1) < minimumVersion)
                throw new InvalidDataException($"Deferred rows for goal '{goalId}' are unavailable.");
            json = reader.GetString(0);
            version = reader.GetInt64(1);
        }

        using var hot = GoalSnapshotByteAccounting.Parse(json);
        if (hot.RootElement.TryGetProperty("IsMetadataOnly", out var stub) && stub.ValueKind == JsonValueKind.True)
        {
            var (archived, utf8Bytes) = await GoalSnapshotArchive.ReadDocumentAsync(connection, goalId, CancellationToken.None);
            using var document = GoalSnapshotByteAccounting.Parse(
                archived ?? throw new InvalidDataException($"Missing archive for goal '{goalId}'."), utf8Bytes);
            return await read(connection, document.RootElement, version);
        }
        return await read(connection, hot.RootElement, version);
    }, CancellationToken.None);

    private static GoalSnapshot CompleteTickSnapshot(GoalSnapshot snapshot, GoalSnapshot stored, long? storedVersion,
        GoalSnapshot? checkpointBaseline = null)
    {
        if (snapshot.TickRowVersion is not { } version) return snapshot;
        var storedTasks = stored.Tasks.ToDictionary(task => task.Id, StringComparer.Ordinal);
        var baselineTasks = checkpointBaseline is { TickRowVersion: null }
            ? checkpointBaseline.Tasks.ToDictionary(task => task.Id, StringComparer.Ordinal)
            : null;
        return snapshot with
        {
            TickRowVersion = null,
            DeferredTimeline = null,
            Timeline = snapshot.DeferredTimeline is null ? snapshot.Timeline
                : snapshot.Timeline.Count == 0 ? stored.Timeline
                : version == storedVersion ? [.. stored.Timeline, .. snapshot.Timeline]
                : MergeTimeline([], stored.Timeline, snapshot.Timeline, preserveStoredOrder: true),
            Tasks = snapshot.Tasks.Select(task =>
            {
                if (task.DeferredHistory is null) return task;
                var persisted = storedTasks.GetValueOrDefault(task.Id);
                var historyBeforeChanges = task.HistoryBeforeChanges;
                // A successful checkpoint advances the request baseline while retaining the live task.
                if (historyBeforeChanges is not null && baselineTasks?.GetValueOrDefault(task.Id) is { } baselineTask)
                    historyBeforeChanges = new(baselineTask.VerificationHistory, baselineTask.DispatchHistory);
                return task with
                {
                    DeferredHistory = null,
                    HistoryBeforeChanges = null,
                    VerificationHistory = CompleteHistory(historyBeforeChanges?.Verification,
                        persisted?.VerificationHistory, task.VerificationHistory, historyBeforeChanges is not null),
                    DispatchHistory = CompleteHistory(historyBeforeChanges?.Dispatch,
                        persisted?.DispatchHistory, task.DispatchHistory, historyBeforeChanges is not null)
                };
            }).ToArray()
        };
    }

    private static IReadOnlyList<T>? CompleteHistory<T>(IReadOnlyList<T>? before, IReadOnlyList<T>? stored,
        IReadOnlyList<T>? current, bool hasChanges)
    {
        if (hasChanges) return PickStoreOwnedList(before, stored, current);
        if (current is not { Count: > 0 }) return stored;
        var keys = (stored ?? []).Select(SnapshotKey).ToHashSet(StringComparer.Ordinal);
        return [.. stored ?? [], .. current.Where(row => keys.Add(SnapshotKey(row)))];
    }
}
