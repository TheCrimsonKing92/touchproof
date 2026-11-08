using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Decides per-pass reservations; receipt holds own their lifetime.
internal static class PendingLandingAdmissionHold
{
    internal sealed record Decision(GoalId GoalId, MergeTrainReceipt Receipt, string Reason);

    internal static IReadOnlyList<Decision> Evaluate(
        IReadOnlyList<Goal> remainingEligible,
        IReadOnlyList<ConductorSpeculativeAcceptanceCandidate> candidates,
        IEnumerable<MergeTrainReceipt> heldReceipts,
        Func<Goal, GateReadyCandidateProjectionResult> project)
    {
        var receipts = heldReceipts.DistinctBy(receipt => receipt.ReceiptId, StringComparer.Ordinal)
            .OrderBy(receipt => receipt.ReceiptId, StringComparer.Ordinal).ToArray();
        if (receipts.Length == 0) return [];

        var members = receipts.SelectMany(receipt => receipt.Identity.Members)
            .Select(member => member.GoalId).ToHashSet();
        var decisions = new List<Decision>();
        foreach (var goal in remainingEligible)
        {
            if (members.Contains(goal.Id)) continue;
            var projection = candidates.FirstOrDefault(candidate => candidate.GoalId == goal.Id)?.ProjectionResult
                ?? project(goal);
            if (projection is not GateReadyCandidateProjectionResult.Ready ready ||
                string.IsNullOrEmpty(ready.Projection.MainRevision))
                continue;

            var receipt = receipts.FirstOrDefault(receipt =>
                string.Equals(ready.Projection.MainRevision, receipt.Identity.ObservedMainRevision,
                    StringComparison.Ordinal));
            if (receipt is not null)
                decisions.Add(new(goal.Id, receipt, $"waiting for pending landing {receipt.ReceiptId}"));
        }
        return decisions;
    }
}
