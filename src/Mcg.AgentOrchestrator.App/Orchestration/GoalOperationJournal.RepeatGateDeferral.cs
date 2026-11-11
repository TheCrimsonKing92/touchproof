using System.Text.Json;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Historical provenance only: the current obligation evidence still determines the hold.
internal sealed record RepeatGateDeferralReceipt(
    string AttemptId, string ReplayContextFingerprint, IReadOnlyList<string> ObligationIds);

internal static partial class GoalOperationJournal
{
    internal const string RepeatGateDeferralOperation = "conductor:operator-prelanding-repeat-gate";

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
