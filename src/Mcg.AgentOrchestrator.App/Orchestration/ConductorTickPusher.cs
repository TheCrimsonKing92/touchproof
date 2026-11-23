using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

public static class ConductorTickPusher
{
    internal const int MaxPersistedProgressLines = 32;
    internal const int MaxPersistedProgressLineChars = 240;

    internal static void Publish(BatchTickSummary summary, GoalSnapshotByteAccounting snapshotBytes,
        Action<BatchTickSummary>? onTick, bool pendingReadWork)
    {
        // The activation sweep can load worker-usage snapshots after receipt delivery.
        var (deserialized, serialized) = snapshotBytes.Seal(pendingReadWork);
        ConductorBatchLoop.EmitProgress(FormattableString.Invariant(
            $"TICK_LOAD_COUNTERS tick={summary.Tick} {ConductorTickStepLedger.FormatSnapshotByteCounters(deserialized, serialized)}"));
        onTick?.Invoke(summary with
        {
            GoalSnapshotDeserializedUtf8Bytes = deserialized,
            GoalSnapshotSerializedUtf8Bytes = serialized
        });
    }

    public static Action<BatchTickSummary> CreateStoreCallback(string runEventStorePath)
    {
        return tick => TryRecord(runEventStorePath, tick);
    }

    public static void TryRecord(string runEventStorePath, BatchTickSummary tick)
    {
        try
        {
            var dto = ToDto(tick);
            var store = new SqliteRunEventStore(runEventStorePath);
            store.AppendAsync(new RunEventAppend(
                RunEventTypes.ConductorTick,
                GoalId: null,
                Operation: "conduct:tick",
                Status: tick.WatchSleeping ? "Sleeping" : "Active",
                Detail: $"tick={tick.Tick} advanced={tick.Advanced} held={tick.Held} escalated={tick.Escalated} retried={tick.Retried} done={tick.Done}",
                PayloadJson: JsonSerializer.Serialize(dto),
                OccurredAt: DateTimeOffset.UtcNow))
                .GetAwaiter()
                .GetResult();
        }
        catch
        {
            // Observability is advisory; never fail the conductor because a tick event write failed.
        }
    }

    private static object ToDto(BatchTickSummary tick)
    {
        var progressLines = CompactProgressLines(tick.ProgressLines);
        return new
        {
            tick.Tick,
            tick.Advanced,
            tick.Held,
            tick.Escalated,
            tick.Retried,
            tick.Done,
            tick.WatchSleeping,
            goal_snapshot_deserialized_utf8_bytes = tick.GoalSnapshotDeserializedUtf8Bytes,
            goal_snapshot_serialized_utf8_bytes = tick.GoalSnapshotSerializedUtf8Bytes,
            progressLines,
            operatorDispositionCount = tick.OperatorDispositions?.Count ?? 0
        };
    }

    private static IReadOnlyList<string> CompactProgressLines(IReadOnlyList<string>? progressLines)
    {
        if (progressLines is null || progressLines.Count == 0)
        {
            return [];
        }

        return progressLines
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Take(MaxPersistedProgressLines)
            .Select(line => line.Length <= MaxPersistedProgressLineChars
                ? line
                : line[..MaxPersistedProgressLineChars])
            .ToList();
    }
}
