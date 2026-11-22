using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed record AcceptanceGateOutcomeObservation(
    string AttemptId, string Directory, DateTimeOffset RecordedAt, DateTimeOffset StartedAt,
    string? LaunchMainCommit, string Mode, string? FullGateReason, int? ExecutedClassCount,
    double SelectionMilliseconds, string Outcome)
{
    public string Kind => AcceptanceGateOutcomeLedger.GateKind;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool HasSoloMetadata { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? OwnerFallbackLaunchMainCommit { get; init; }
}

internal sealed record SoloGateOutcomeProvenance(DateTimeOffset StartedAt, string? LaunchMainCommit, string Outcome);

internal static class AcceptanceGateOutcomeLedger
{
    internal const string FileName = "cost-goal-outcome-gates.jsonl";
    internal const string AttemptRoot = "acceptance-gate-attempts";
    internal const string GateKind = "gate";
    internal const string InvalidationKind = "invalidation";
    internal const string BackfillKind = "backfill-complete";
    private const string PlanTargetPrefix = "GATE_LANE_PLAN ";
    private static readonly Regex GateAttemptId = new(@"-(?<time>[0-9]{17})-[0-9a-f]{32}$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
    };

    internal static DateTimeOffset? RecordedAt(string attemptId)
    {
        var match = GateAttemptId.Match(attemptId);
        return match.Success && DateTimeOffset.TryParseExact(match.Groups["time"].Value, "yyyyMMddHHmmssfff",
            CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var time) ? time : null;
    }

    internal static AcceptanceSelectedExecutionRecord? ReadSelection(AcceptanceGateProgress progress, string attemptId)
    {
        if (!progress.CurrentTarget.StartsWith(PlanTargetPrefix, StringComparison.Ordinal) ||
            !File.Exists(progress.HeartbeatPath)) return null;
        try
        {
            var plan = JsonSerializer.Deserialize<AcceptanceGateLanePlan>(
                progress.CurrentTarget.AsSpan(PlanTargetPrefix.Length), AcceptanceGateLanePlan.JsonOptions);
            // Match OwnerConsoleGateLanePlanReader, which validates the historical lane plans.
            if (plan is null || plan.SchemaVersion != AcceptanceGateLanePlan.CurrentSchemaVersion ||
                plan.AttemptId != attemptId || plan.Scheduled is null || plan.Skipped is null ||
                plan.Scheduled.Any(string.IsNullOrWhiteSpace) ||
                plan.Skipped.Any(row => row is null || string.IsNullOrWhiteSpace(row.Name) ||
                    string.IsNullOrWhiteSpace(row.Reason))) return null;
            var names = plan.Scheduled.Concat(plan.Skipped.Select(row => row.Name)).ToArray();
            return names.Distinct(StringComparer.Ordinal).Count() == names.Length ? plan.SelectedExecution : null;
        }
        catch (JsonException) { return null; }
    }

    internal static bool IsRecordedGate(ConductorParallelAcceptanceRunArtifact result) =>
        result is { Kind: "accepted", DispatchKind: GateKind, Acceptance.TestResultPaths.Count: > 0 };

    internal static AcceptanceGateOutcomeObservation CreateObservation(string prefix, DateTimeOffset recordedAt,
        ConductorParallelAcceptanceRunArtifact result, AcceptanceSelectedExecutionRecord selection,
        SoloGateOutcomeProvenance? solo = null)
    {
        var attemptId = Path.GetFileName(prefix);
        var ownerMain = attemptId.Contains("-gate-", StringComparison.Ordinal) ? result.MainHeadSha : null;
        var launchCommit = solo is not null ? solo.LaunchMainCommit : ownerMain;
        var outcome = solo?.Outcome ?? (result.Acceptance!.Passed ? "Passed" : "Failed");
        return new(attemptId, Path.GetFileName(Path.GetDirectoryName(prefix))!, recordedAt,
            solo?.StartedAt ?? recordedAt, launchCommit, selection.Mode, selection.FullGateReason,
            selection.ExecutedClassCount, selection.SelectionMilliseconds, outcome)
        {
            HasSoloMetadata = solo is not null,
            OwnerFallbackLaunchMainCommit = solo is not null ? ownerMain : null
        };
    }

    internal static void Publish(string prefix, ConductorParallelAcceptanceRunArtifact result,
        AcceptanceSelectedExecutionRecord? selection, SoloGateOutcomeProvenance? solo = null)
    {
        if (selection is null || !IsRecordedGate(result) || RecordedAt(Path.GetFileName(prefix)) is not { } recordedAt) return;
        Append(prefix, CreateObservation(prefix, recordedAt, result, selection, solo));
    }

    internal static void Invalidate(string prefix) => Append(prefix,
        new { Kind = InvalidationKind, AttemptId = Path.GetFileName(prefix), Directory = Path.GetFileName(Path.GetDirectoryName(prefix)) });

    private static void Append<T>(string prefix, T row)
    {
        var root = Path.GetDirectoryName(Path.GetDirectoryName(prefix));
        if (Path.GetFileName(root) != AttemptRoot) return;
        var path = Path.Combine(Path.GetDirectoryName(root!)!, FileName);
        try { SharedJsonlFile.AppendLine(path, JsonSerializer.Serialize(row, Json)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            System.Diagnostics.Trace.TraceWarning($"Acceptance gate outcome ledger unavailable for {prefix}: {ex.Message}");
        }
    }
}
