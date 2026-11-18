using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class MaintenanceStepReceipts
{
    internal const string GoalSnapshotArchiveOperation = "goal-snapshot-archive:drain";
    internal const string ArchivedSnapshotCompressionOperation = "archived-snapshot-compression:drain";

    internal static MaintenanceStepReceipt? ReadLatest(SqliteRunEventStore store, string operation)
    {
        try
        {
            var receipt = store.ReadLatestAsync(RunEventTypes.EvidenceRetention, operation)
                .GetAwaiter().GetResult();
            return receipt is null ? null : new MaintenanceStepReceipt(
                receipt.OccurredAt,
                !string.Equals(receipt.Status, "Completed", StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return null;
        }
    }

    internal static bool IsDue(MaintenanceStepReceipt? receipt, DateTimeOffset now) =>
        receipt is null || receipt.Failed || now - receipt.OccurredAt >= RunEventMaintenanceCadence.Interval;

    internal static DateTimeOffset? NextDue(MaintenanceStepReceipt? receipt) =>
        receipt is { Failed: false } ? receipt.OccurredAt.Add(RunEventMaintenanceCadence.Interval) : null;

    internal static void AppendArchive(
        SqliteRunEventStore store, GoalSnapshotArchiveCadenceResult result, DateTimeOffset completedAt) =>
        Append(store, GoalSnapshotArchiveOperation, result.Status, GoalSnapshotArchiveCadence.Format(result),
            JsonSerializer.Serialize(result), completedAt);

    internal static void AppendCompression(
        SqliteRunEventStore store, ArchivedSnapshotCompressionCadenceResult result, DateTimeOffset completedAt) =>
        Append(store, ArchivedSnapshotCompressionOperation, result.Status,
            ArchivedSnapshotCompressionCadence.Format(result), JsonSerializer.Serialize(result), completedAt);

    private static void Append(
        SqliteRunEventStore store, string operation, string status, string detail, string payloadJson,
        DateTimeOffset completedAt) =>
        store.AppendAsync(new RunEventAppend(
            RunEventTypes.EvidenceRetention,
            GoalId: null,
            Operation: operation,
            Status: status == "failed" ? "Failed" : "Completed",
            Detail: detail,
            PayloadJson: payloadJson,
            OccurredAt: completedAt)).GetAwaiter().GetResult();
}

internal sealed record MaintenanceStepReceipt(DateTimeOffset OccurredAt, bool Failed);
