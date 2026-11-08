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
            var row = JsonSerializer.Deserialize<ExecutorSlotDecisionRecord>(line, JsonOptions);
            if (row is null || row.Version is not (1 or 2) || string.IsNullOrWhiteSpace(row.ExecutorId) ||
                row.Decision is null)
                throw new InvalidOperationException("invalid-executor-slot-decision-ledger-row");
            var state = row.ControllerState;
            if (row.Version == 1)
            {
                // V1 stored both states and a lookback-sized id set. Migrate once on restart.
                using var document = JsonDocument.Parse(line);
                var element = row.ApplicationStatus == "pending-handler"
                    ? document.RootElement.GetProperty("decision").GetProperty("next_state")
                    : document.RootElement.GetProperty("before_state");
                state = element.Deserialize<ExecutorSlotControllerState>(JsonOptions)
                    ?? throw new InvalidOperationException("invalid-executor-slot-legacy-state");
                state = state with { CompletedThrough = new(row.ObservedAt, "\uffff") };
            }
            if (state?.Settings is null)
                throw new InvalidOperationException("invalid-executor-slot-controller-state");
            state.Settings.Validate();
            row = row with { ControllerState = state, Decision = row.Decision with { NextState = state } };
            latest[row.ExecutorId] = row;
        }
        return latest;
    }

    internal static ExecutorSlotControllerState? Restore(ExecutorSlotDecisionRecord? row, int observedSlots)
    {
        if (row is null) return null;
        // Pending is an audit authorization, never a claim that the handler completed. Reconcile the actual setting.
        if (row.ApplicationStatus == "pending-handler" && observedSlots != row.Decision.ProposedSlots)
            return null;
        return row.ControllerState;
    }
}
