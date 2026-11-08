using System.Text.Json;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class ExecutorSlotDecisionLedger
{
    internal const string FileName = "executor-slot-decisions.jsonl";
    internal static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    internal static void Append(string path, ExecutorSlotDecisionRecord row)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        SharedJsonlFile.AppendLine(path, JsonSerializer.Serialize(row, JsonOptions));
    }

    internal static IReadOnlyDictionary<string, ExecutorSlotDecisionRecord> ReadLatest(string path)
    {
        var latest = new Dictionary<string, ExecutorSlotDecisionRecord>(StringComparer.Ordinal);
        foreach (var line in SharedJsonlFile.ReadLines(path))
        {
            try
            {
                var row = JsonSerializer.Deserialize<ExecutorSlotDecisionRecord>(line, JsonOptions);
                if (row is not { Version: 1 } || string.IsNullOrWhiteSpace(row.ExecutorId) ||
                    row.Decision?.NextState?.Settings is null || row.BeforeState?.Settings is null) continue;
                row.Decision.NextState.Settings.Validate();
                row.BeforeState.Settings.Validate();
                latest[row.ExecutorId] = row;
            }
            catch (Exception error) when (error is JsonException or InvalidOperationException) { }
        }
        return latest;
    }

    internal static ExecutorSlotControllerState? Restore(ExecutorSlotDecisionRecord? row, int observedSlots)
    {
        if (row is null) return null;
        // Pending is an audit authorization, never a claim that the handler completed. Reconcile the actual setting.
        if (row.ApplicationStatus == "pending-handler" && observedSlots == row.Decision.ProposedSlots)
            return row.Decision.NextState;
        return row.BeforeState;
    }
}
