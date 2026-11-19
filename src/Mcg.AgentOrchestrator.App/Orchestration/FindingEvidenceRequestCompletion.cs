using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Completed request decisions require candidate execution covering the member's remaining selections.
internal static class FindingEvidenceRequestCompletion
{
    internal static bool IsPartialGreenDisposition(FindingEvidenceReceipt receipt, FindingEvidenceRequestDisposition disposition) =>
        disposition is { Disposition: "incomplete", Decision: FindingEvidenceRequestDecision.PendingExecution,
            Reason: "candidate-selections-not-executed" } &&
        disposition.SourceReceiptIds?.Contains(receipt.ReceiptId, StringComparer.Ordinal) == true;

    internal static bool TryPersistDispositions(
        ConductorParallelAcceptanceAttemptCoordinator coordinator, ConductorParallelAcceptanceAttempt? attempt,
        string round, string receiptId, IReadOnlyList<FindingEvidenceRequestDisposition> dispositions,
        out FailedGoalFindingObservation decision)
    {
        decision = FailedGoalFindingObservation.None;
        if (attempt is not null && coordinator.RecordFocusedEvidenceRequestDispositions(
                attempt, round, receiptId, dispositions)) return true;
        decision = FailedGoalFindingObservation.Observed(
            FailedGoalFindingObservationKind.FindingEvidenceReceiptPersistenceFailed,
            $"Focused evidence receipt {receiptId} could not be published because its per-request apparatus dispositions could not be persisted; downstream routing stopped.");
        return false;
    }

    internal static IReadOnlyList<FindingEvidenceRequestDisposition> BuildDispositions(
        IReadOnlyList<FindingEvidenceRequestDisposition> initialRequestDispositions,
        FindingEvidenceBatch batch,
        FindingEvidenceReceipt receipt,
        Func<FindingEvidenceSelection, string> formatSelection,
        IEnumerable<FindingEvidenceBatch>? supersededByActionableRed = null)
    {
        var executedCoverage = FindingEvidenceExecutedCoverage.ExecutedSelections(receipt, formatSelection)
            .ToHashSet();
        var candidateExecuted = (receipt.Arms ?? []).Any(arm =>
            arm is { Arm: FindingEvidenceArm.Candidate, Accepted: true, ExecutedTestCount: > 0 });
        var completedDispositions = initialRequestDispositions.Select(disposition =>
        {
            if (!disposition.Disposition.StartsWith("executed-", StringComparison.Ordinal)) return disposition;
            var member = batch.Members.Single(member => member.Identity == disposition.RequestIdentity);
            // The member contains only its uncovered remainder after prior receipt reuse.
            // Acceptance and empty omission metadata do not prove execution: a zero-test
            // apparatus result still has a receipt. Require candidate execution as well.
            var completed = candidateExecuted && executedCoverage.IsSupersetOf(
                FindingEvidenceRoundPlanner.ExpandSelections(member.TypedRequest.Selections));
            return completed
                ? disposition with
                {
                    Decision = disposition.CoveringRequestIdentity is null
                        ? FindingEvidenceRequestDecision.Executed : FindingEvidenceRequestDecision.CoveredByReceipt,
                    Reason = disposition.CoveringRequestIdentity is null ? disposition.Reason : "covered-by-executed-superset",
                    SourceReceiptIds = (disposition.SourceReceiptIds ?? []).Append(receipt.ReceiptId)
                        .Distinct(StringComparer.Ordinal).ToArray()
                }
                : disposition with
                {
                    Disposition = "incomplete",
                    Decision = supersededByActionableRed is not null
                        ? FindingEvidenceRequestDecision.DeferredToNextCandidate
                        : FindingEvidenceRequestDecision.PendingExecution,
                    Reason = "candidate-selections-not-executed",
                    CoveringRequestIdentity = null,
                    SourceReceiptIds = (disposition.SourceReceiptIds ?? []).Append(receipt.ReceiptId)
                        .Distinct(StringComparer.Ordinal).ToArray()
                };
        });
        if (supersededByActionableRed is null) return completedDispositions.ToArray();

        return completedDispositions.Where(disposition =>
                batch.Members.Any(member => member.Identity == disposition.RequestIdentity))
            .Concat(supersededByActionableRed.SelectMany(superseded => superseded.Members).SelectMany(member =>
                member.Findings.Select(finding => new FindingEvidenceRequestDisposition(
                    finding.StableId, member.Identity, "superseded", "superseded-by-actionable-red",
                    FindingEvidenceRequestDecision.Superseded, receipt.CandidateSha, receipt.FindingRoundFingerprint,
                    SourceReceiptIds: [receipt.ReceiptId], ExecutionBasisIdentity: receipt.ExecutionBasisIdentity))))
            .ToArray();
    }
}
