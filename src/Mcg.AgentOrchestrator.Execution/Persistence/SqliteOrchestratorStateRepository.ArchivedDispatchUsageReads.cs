using System.Globalization;
using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Microsoft.Data.Sqlite;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed record ArchivedDispatchUsage(
    string GoalId, string TaskId, int Sequence, AgentRole Role, TaskDispatchRecord Dispatch, DateTimeOffset ArchivedAt);

public sealed partial class SqliteOrchestratorStateRepository
{
    internal Task<IReadOnlyList<ArchivedDispatchUsage>> ReadArchivedDispatchUsageAsync(
        Func<AgentRole, DateTimeOffset, bool> eligible, CancellationToken token = default) =>
        WithBusyRetryAsync<IReadOnlyList<ArchivedDispatchUsage>>(async () =>
        {
            await using var connection = OpenConnection();
            if (!await ProviderHoldSeedEvidenceStore.HasArchiveAsync(connection, token))
                return [];

            var dispatches = new List<ArchivedDispatchUsage>();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT goal_id, snapshot_json, archived_at FROM goal_snapshot_archive ORDER BY goal_id";
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                var goalId = reader.GetString(0);
                try
                {
                    using var stream = GoalSnapshotArchiveCodec.OpenDecodedStream(reader.GetValue(1))
                        ?? throw new InvalidDataException("Snapshot payload is missing.");
                    var goal = await JsonSerializer.DeserializeAsync<ArchivedUsageGoal>(stream, SerializerOptions, token);
                    if (goal is null || goal.Id != goalId || goal.IsMetadataOnly || goal.Tasks is null)
                        throw new InvalidDataException("Snapshot identity or task collection is invalid.");
                    var archivedAt = DateTimeOffset.Parse(reader.GetString(2), CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal);
                    foreach (var task in goal.Tasks)
                    {
                        var history = await ReadArchivedUsageHistoryAsync(connection, goalId, task, token);
                        for (var sequence = 0; sequence < history.Count; sequence++)
                        {
                            var dispatch = history[sequence];
                            if (eligible(task.RequiredRole, dispatch.DispatchedAt))
                                dispatches.Add(new(goalId, task.Id, sequence, task.RequiredRole, dispatch.ToRecord(), archivedAt));
                        }
                    }
                }
                catch (Exception ex) when (ex is JsonException or InvalidDataException or FormatException)
                {
                    throw new InvalidDataException($"Cannot read dispatch usage for archived goal '{goalId}': {ex.Message}", ex);
                }
            }
            return dispatches;
        }, token);

    private static async Task<List<ArchivedUsageDispatch>> ReadArchivedUsageHistoryAsync(
        SqliteConnection connection, string goalId, ArchivedUsageTask task, CancellationToken token)
    {
        var latest = task.LastDispatch;
        var history = new List<ArchivedUsageDispatch>(task.DispatchHistory ?? []);
        if (task.HistoryInTable.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null))
        {
            var stored = await TaskHistoryStore.ReadMetadataAsync<JsonElement, ArchivedUsageDispatch>(
                connection, goalId, task.Id, default, default, task.HistoryInTable,
                _ => "json_object()", ProjectArchivedUsageDispatch, token);
            history = stored.Dispatch.Entries.Select(ReadDispatch).ToList();
            if (stored.LatestDispatch.ValueKind != JsonValueKind.Undefined)
                latest = ReadDispatch(stored.LatestDispatch);
        }

        // Match TaskSpec's dispatch sequence: the latest copy replaces the first history
        // entry at its timestamp, or appends if legacy storage only wrote LastDispatch.
        if (latest is not null)
        {
            var index = history.FindIndex(dispatch => dispatch.DispatchedAt == latest.DispatchedAt);
            if (index < 0)
                history.Add(latest);
            else
                history[index] = latest;
        }
        return history;

        static ArchivedUsageDispatch ReadDispatch(JsonElement entry) =>
            entry.Deserialize<ArchivedUsageDispatch>(SerializerOptions)
            ?? throw new InvalidDataException("Dispatch history entry is missing.");
    }

    private static string ProjectArchivedUsageDispatch(string expression)
    {
        var fields = GoalTaskHistoryStore.MetadataSql(expression, ["DispatchedAt", "ModelName", "ProviderUsage"]);
        var receipt = $"(({expression}) -> '$.ContextPackageReceipt')";
        var receiptFields = GoalTaskHistoryStore.MetadataSql(receipt,
            ["InputTokens", "CachedInputTokens", "OutputTokens", "Turns", "ToolCalls", "ModelInputTokenEstimate", "WtCalls"]);
        return $"""
            json_set({fields}, '$.ContextPackageReceipt',
                json(CASE WHEN json_type({receipt}) = 'object' THEN {receiptFields} ELSE {receipt} END))
            """;
    }

    private sealed record ArchivedUsageGoal(string Id, bool IsMetadataOnly, IReadOnlyList<ArchivedUsageTask>? Tasks);

    private sealed record ArchivedUsageTask(string Id, AgentRole RequiredRole, ArchivedUsageDispatch? LastDispatch,
        IReadOnlyList<ArchivedUsageDispatch>? DispatchHistory, JsonElement HistoryInTable);

    private sealed record ArchivedUsageDispatch(DateTimeOffset DispatchedAt, string? ModelName,
        DispatchProviderUsage? ProviderUsage, ArchivedUsageReceipt? ContextPackageReceipt)
    {
        internal TaskDispatchRecord ToRecord() => new("", "", "", DispatchedAt, ModelName: ModelName,
            ProviderUsage: ProviderUsage, ContextPackageReceipt: ContextPackageReceipt?.ToReceipt());
    }

    private sealed record ArchivedUsageReceipt(ProviderUsageValue InputTokens, ProviderUsageValue CachedInputTokens,
        ProviderUsageValue OutputTokens, ProviderUsageValue? Turns, ProviderUsageValue? ToolCalls, int? ModelInputTokenEstimate,
        IReadOnlyDictionary<string, long>? WtCalls)
    {
        internal WorkerContextPackageReceipt ToReceipt()
        {
            var receipt = new WorkerContextPackageReceipt("", [], InputTokens, CachedInputTokens, OutputTokens,
                ModelInputTokenEstimate: ModelInputTokenEstimate ?? 0, Turns: Turns)
            {
                WtCalls = WtCalls
            };
            return ToolCalls is { } toolCalls ? receipt with { ToolCalls = toolCalls } : receipt;
        }
    }
}
