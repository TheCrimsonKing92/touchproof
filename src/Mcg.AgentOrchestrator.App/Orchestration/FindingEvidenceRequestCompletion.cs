using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using static Mcg.AgentOrchestrator.App.Orchestration.ConductorDriver;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Completed request decisions require candidate execution covering the member's remaining selections.
internal static class FindingEvidenceRequestCompletion
{
    internal static bool IsPartialGreenDisposition(FindingEvidenceReceipt receipt, FindingEvidenceRequestDisposition disposition) =>
        disposition.Reason == "candidate-selections-not-executed" && IsIncompleteExecution(receipt, disposition);

    internal static bool IsIncompleteExecution(FindingEvidenceReceipt receipt, FindingEvidenceRequestDisposition disposition) =>
        disposition is { Disposition: "incomplete", Decision: FindingEvidenceRequestDecision.PendingExecution } &&
        disposition.SourceReceiptIds?.Contains(receipt.ReceiptId, StringComparer.Ordinal) == true;

    internal static bool IsExecutedMember(FindingEvidenceReceipt receipt, FindingEvidenceRequestDisposition disposition,
        string requestIdentity, string findingId) =>
        disposition.Decision == FindingEvidenceRequestDecision.Executed &&
        disposition.RequestIdentity == requestIdentity && disposition.FindingStableId == findingId &&
        disposition.SourceReceiptIds?.Contains(receipt.ReceiptId, StringComparer.Ordinal) == true;

    internal static bool HasCurrentGreenReceipt(
        TaskSpec requestingTask,
        ReviewFinding finding,
        FindingEvidenceRequest request,
        string candidateSha,
        string findingRoundFingerprint,
        string executionBasisIdentity,
        IReadOnlyList<PreTesterCoverageComposition.PreTesterCoverageSource> preTesterCoverage)
    {
        var receiptId = finding.EvidenceOutcome?.ReceiptId;
        if (string.IsNullOrWhiteSpace(receiptId) || candidateSha == "unavailable")
        {
            return false;
        }

        var identity = BuildFindingEvidenceIdentity(request);
        if (!TryGetFindingEvidenceCoverage(request, out var requestedCoverage))
        {
            return false;
        }

        var receipt = requestingTask.VerificationHistory
            .SelectMany(verification => verification.FindingEvidenceReceipts ?? [])
            .LastOrDefault(item => item.ReceiptId == receiptId &&
                item.FindingRoundFingerprint == findingRoundFingerprint &&
                IsReusableGreenFindingEvidenceReceipt(item, candidateSha, executionBasisIdentity));
        if (receipt is null ||
            !TryGetFindingEvidenceCoverage(FindingEvidenceExecutedCoverage.ExecutedRequest(receipt, FormatFindingEvidenceSelection),
                out var executedCoverage))
            return false;

        var memberDispositions = (receipt.RequestDispositions ?? []).Where(disposition =>
            disposition.RequestIdentity == identity && disposition.FindingStableId == finding.StableId).ToArray();
        if (requestedCoverage.SetEquals(executedCoverage) &&
            (BuildFindingEvidenceIdentity(receipt.Request) == identity || memberDispositions.Length > 0))
            return true;

        var executedMember = memberDispositions.FirstOrDefault(disposition =>
            IsExecutedMember(receipt, disposition, identity, finding.StableId));
        if (executedMember is null) return false;
        if (requestedCoverage.IsSubsetOf(executedCoverage)) return true;

        // Recheck the original contributors against current per-selection authority.
        // A current execution earns credit only if it still contributes to the complete request.
        return executedMember.SourceReceiptIds is { Count: > 1 } sourceIds &&
            TryResolveFindingEvidenceCoverage(requestingTask, request, candidateSha, executionBasisIdentity,
                out var contributors, out var uncovered, out _, preTesterCoverage) &&
            uncovered.Count == 0 &&
            contributors.Any(source => source.ReceiptId == receipt.ReceiptId) &&
            contributors.All(source => sourceIds.Contains(source.ReceiptId, StringComparer.Ordinal));
    }

    // Composition can invalidate a reused source after the RED repair route has been selected.
    internal static FindingEvidenceReceipt DeferIncompleteMembers(FindingEvidenceReceipt receipt) =>
        receipt with { RequestDispositions = (receipt.RequestDispositions ?? []).Select(disposition =>
            IsIncompleteExecution(receipt, disposition)
                ? disposition with { Decision = FindingEvidenceRequestDecision.DeferredToNextCandidate }
                : disposition).ToArray() };

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
