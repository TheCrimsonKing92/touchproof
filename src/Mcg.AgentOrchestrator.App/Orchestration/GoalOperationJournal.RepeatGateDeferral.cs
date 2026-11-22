using System.Text.Json;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Historical provenance only: the current obligation evidence still determines the hold.
internal sealed record RepeatGateDeferralReceipt(
    string AttemptId, string ReplayContextFingerprint, IReadOnlyList<string> ObligationIds);

internal static partial class GoalOperationJournal
{
    internal const string RepeatGateDeferralOperation = "conductor:operator-prelanding-repeat-gate";
    private const string RefusedCarryHoldOperation = "conductor:criterion-evidence-refused-carry-hold";

    internal sealed record RefusedCarryHistory(
        string BranchSha, string? MainSha, IReadOnlyList<AcceptanceCriterionEvidence.RefusedCarryBinding> Bindings,
        string Diagnostic, string? AttemptId);

    internal static void RecordRefusedCarryHold(string? executionDirectory, Goal goal,
        string branchSha, string? mainSha, IReadOnlyList<CriterionEvidenceObligation> bindings, string diagnostic,
        string? attemptId = null)
    {
        if (string.IsNullOrWhiteSpace(executionDirectory)) return;
        var history = new RefusedCarryHistory(branchSha, mainSha,
            bindings.OrderBy(item => item.Id, StringComparer.Ordinal)
                .Select(AcceptanceCriterionEvidence.BindingOf).ToArray(), diagnostic, attemptId);
        var instance = RefusedCarryPairKey(branchSha, mainSha);
        var detail = JsonSerializer.Serialize(history, JsonOptions);
        if (ReadActive(executionDirectory, goal.Id).Entries.Any(entry =>
                entry.Operation == RefusedCarryHoldOperation && entry.OperationInstanceId == instance &&
                entry.Detail == detail))
            return;
        Append(executionDirectory, goal.Id, Key(goal.Id, RefusedCarryHoldOperation),
            RefusedCarryHoldOperation, GoalOperationStatus.Completed, detail, branchSha, mainSha, null,
            operationInstanceId: instance);
    }

    internal static IEnumerable<RefusedCarryHistory> ReadRefusedCarryHolds(string executionDirectory, GoalId goalId)
    {
        foreach (var entry in ReadActive(executionDirectory, goalId).Entries.Reverse())
        {
            // Earlier entries used plain diagnostic text. Those entries still identify a pair for replay.
            if (entry.Operation == RefusedCarryHoldOperation && entry.Status == GoalOperationStatus.Completed &&
                entry.BranchHeadSha is not null && entry.Detail is not null &&
                JsonSerializer.Deserialize<RefusedCarryHistory>(entry.Detail, JsonOptions) is { } history)
                yield return history;
        }
    }

    internal static bool HasRefusedCarryHoldForPair(string executionDirectory, Goal goal,
        ConductorParallelAcceptanceAttempt attempt) =>
        ReadActive(executionDirectory, goal.Id).Entries.Any(entry =>
            entry.Operation == RefusedCarryHoldOperation && entry.Status == GoalOperationStatus.Completed &&
            entry.OperationInstanceId == RefusedCarryPairKey(attempt.BranchHeadSha, attempt.MainHeadSha) &&
            (entry.BranchHeadSha is null
                ? entry.At >= attempt.StartedAt
                : entry.Detail is not null &&
                  JsonSerializer.Deserialize<RefusedCarryHistory>(entry.Detail, JsonOptions)?.AttemptId == attempt.AttemptId));

    private static string RefusedCarryPairKey(string? branchSha, string? mainSha) =>
        $"{branchSha?.ToLowerInvariant()}:{mainSha?.ToLowerInvariant()}";

    internal static bool HasFullGateDeferral(string executionDirectory, Goal goal,
        ConductorParallelAcceptanceAttempt attempt) =>
        HasRepeatGateDeferral(executionDirectory, goal, attempt) ||
        HasRefusedCarryHoldForPair(executionDirectory, goal, attempt);

    internal static void RepeatGateDeferred(string executionDirectory, Goal goal,
        ConductorParallelAcceptanceAttempt attempt, IReadOnlyList<CriterionEvidenceObligation> blockers)
    {
        if (HasRepeatGateDeferral(executionDirectory, goal, attempt)) return;
        var receipt = new RepeatGateDeferralReceipt(attempt.AttemptId, attempt.ReplayContextFingerprint!,
            blockers.Select(item => item.Id).Order(StringComparer.Ordinal).ToArray());
        Append(executionDirectory, goal.Id, $"{Key(goal.Id, RepeatGateDeferralOperation)}:{attempt.AttemptId}",
            RepeatGateDeferralOperation, GoalOperationStatus.Completed, JsonSerializer.Serialize(receipt, JsonOptions),
            attempt.BranchHeadSha, attempt.MainHeadSha, null);
    }

    internal static bool HasRepeatGateDeferral(string executionDirectory, Goal goal,
        ConductorParallelAcceptanceAttempt attempt) =>
        Read(executionDirectory, goal.Id).Entries.Any(entry =>
            entry.Operation == RepeatGateDeferralOperation && entry.Status == GoalOperationStatus.Completed &&
            entry.HasCandidate(attempt.BranchHeadSha, attempt.MainHeadSha) && entry.Detail is not null &&
            JsonSerializer.Deserialize<RepeatGateDeferralReceipt>(entry.Detail, JsonOptions) is { ObligationIds.Count: > 0 } receipt &&
            receipt.AttemptId == attempt.AttemptId && receipt.ReplayContextFingerprint == attempt.ReplayContextFingerprint);
}
