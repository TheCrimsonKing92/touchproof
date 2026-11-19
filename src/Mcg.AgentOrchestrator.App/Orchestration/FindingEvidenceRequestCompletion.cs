using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using static Mcg.AgentOrchestrator.App.Orchestration.ConductorDriver;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Completed request decisions require candidate execution covering the member's remaining selections.
internal static class FindingEvidenceRequestCompletion
{
    internal static (FindingEvidenceReceipt Receipt, bool UnverifiedReviewerResult) PrepareReceipt(
        Goal goal, TaskSpec task, FindingEvidenceBatch batch, FocusedEvidenceRunResult evidence,
        string receiptId, string candidateSha, string round, string basis, IReadOnlyList<FindingEvidenceArmReceipt> arms,
        IReadOnlyList<FindingEvidenceRequestDisposition> initialDispositions, bool homeRepository,
        IEnumerable<FindingEvidenceBatch>? supersededByActionableRed)
    {
        // Legacy Reviewer delivery can carry a diagnostic summary from an untyped runner,
        // including results with no checks and checks that report no execution count.
        // Qualification still requires candidate execution and cannot use this receipt.
        var unverifiedReviewerResult = task.RequiredRole == AgentRole.Reviewer && !UsesQualifiedReviewerContinuation(goal, task) &&
            homeRepository && evidence is { Accepted: true, OutcomeReason: null, Coverage: null } && arms.Count == 0 &&
            evidence.Checks.All(check => check.ExecutedTestCount is null) &&
            batch.TypedRequest is { NegativeControl: null, RevertPaths: null, Mutation: null } &&
            batch.Members.All(member => member.ReusedSourceReceipts is not { Count: > 0 } && member.CoverageDecisionReason is null);
        var receipt = new FindingEvidenceReceipt(
            receiptId, candidateSha, batch.TypedRequest, evidence.Accepted, evidence.IsValidEvidence && !unverifiedReviewerResult,
            evidence.Summary, arms, FindingRoundFingerprint: round, ExecutionBasisIdentity: basis,
            NegativeControlOutcome: evidence.NegativeControlOutcome, RevertPathsRejection: evidence.RevertPathsRejection);
        var dispositions = BuildDispositions(initialDispositions, batch, receipt, FormatFindingEvidenceSelection, supersededByActionableRed);
        if (unverifiedReviewerResult)
            dispositions = dispositions.Select(disposition => IsIncompleteExecution(receipt, disposition)
                ? disposition with
                {
                    Disposition = "unavailable",
                    Decision = FindingEvidenceRequestDecision.Refused,
                    Reason = FindingRoundBatchCoordinator.UnverifiedReviewerResult
                }
                : disposition).ToArray();
        return (receipt with { RequestDispositions = dispositions }, unverifiedReviewerResult);
    }

    internal static bool IsPartialGreenDisposition(FindingEvidenceReceipt receipt, FindingEvidenceRequestDisposition disposition) =>
        disposition.Reason == "candidate-selections-not-executed" && IsIncompleteExecution(receipt, disposition);

    internal static bool IsIncompleteExecution(FindingEvidenceReceipt receipt, FindingEvidenceRequestDisposition disposition) =>
        disposition is { Disposition: "incomplete", Decision: FindingEvidenceRequestDecision.PendingExecution } &&
        disposition.SourceReceiptIds?.Contains(receipt.ReceiptId, StringComparer.Ordinal) == true;

    internal static FindingEvidenceRequestDisposition? FindSettledMember(
        FindingEvidenceReceipt receipt, string requestIdentity, string findingId) =>
        (receipt.RequestDispositions ?? []).FirstOrDefault(disposition =>
            disposition.Decision is FindingEvidenceRequestDecision.Executed or FindingEvidenceRequestDecision.CoveredByReceipt &&
            disposition.RequestIdentity == requestIdentity && disposition.FindingStableId == findingId &&
            disposition.SourceReceiptIds?.Contains(receipt.ReceiptId, StringComparer.Ordinal) == true);

    internal static FindingEvidenceReceipt? FindCurrentCompletedReceipt(TaskSpec task, ReviewFinding finding,
        FindingEvidenceRequest request, string candidate, string round, string basis,
        IReadOnlyList<PreTesterCoverageComposition.PreTesterCoverageSource> preTesterCoverage) =>
        FindCurrentGreenReceipt(task, finding, request, candidate, round, basis, preTesterCoverage) ??
        FindCurrentVacuousReceipt(task, finding, BuildFindingEvidenceIdentity(request), candidate, round, basis);

    private static FindingEvidenceReceipt? FindCurrentVacuousReceipt(TaskSpec task, ReviewFinding finding,
        string requestIdentity, string candidate, string round, string basis)
    {
        if (finding.EvidenceOutcome is not { Honoured: true, ResultReason: FindingEvidenceOutcomeReason.VacuousEvidence,
                ReceiptId: { } receiptId })
            return null;

        return task.VerificationHistory.SelectMany(record => record.FindingEvidenceReceipts ?? []).LastOrDefault(receipt =>
            receipt is { Accepted: true, Passed: false } && receipt.ReceiptId == receiptId &&
            string.Equals(receipt.CandidateSha, candidate, StringComparison.OrdinalIgnoreCase) &&
            receipt.FindingRoundFingerprint == round && receipt.ExecutionBasisIdentity == basis &&
            FindSettledMember(receipt, requestIdentity, finding.StableId) is not null);
    }

    private static FindingEvidenceReceipt? FindCurrentGreenReceipt(
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
            return null;
        }

        var identity = BuildFindingEvidenceIdentity(request);
        if (!TryGetFindingEvidenceCoverage(request, out var requestedCoverage))
        {
            return null;
        }

        var receipt = requestingTask.VerificationHistory
            .SelectMany(verification => verification.FindingEvidenceReceipts ?? [])
            .LastOrDefault(item => item.ReceiptId == receiptId &&
                item.FindingRoundFingerprint == findingRoundFingerprint &&
                IsReusableGreenFindingEvidenceReceipt(item, candidateSha, executionBasisIdentity));
        if (receipt is null ||
            !TryGetFindingEvidenceCoverage(FindingEvidenceExecutedCoverage.ExecutedRequest(receipt, FormatFindingEvidenceSelection),
                out var executedCoverage))
            return null;

        var memberDispositions = (receipt.RequestDispositions ?? []).Where(disposition =>
            disposition.RequestIdentity == identity && disposition.FindingStableId == finding.StableId).ToArray();
        if (requestedCoverage.SetEquals(executedCoverage) &&
            (BuildFindingEvidenceIdentity(receipt.Request) == identity || memberDispositions.Length > 0))
            return receipt;

        var settledMember = FindSettledMember(receipt, identity, finding.StableId);
        if (settledMember is null) return null;
        if (requestedCoverage.IsSubsetOf(executedCoverage)) return receipt;

        // Recheck the original contributors against current per-selection authority.
        // A current execution earns credit only if it still contributes to the complete request.
        return settledMember.SourceReceiptIds is { Count: > 1 } sourceIds &&
            TryResolveFindingEvidenceCoverage(requestingTask, request, candidateSha, executionBasisIdentity,
                out var contributors, out var uncovered, out _, preTesterCoverage) &&
            uncovered.Count == 0 &&
            contributors.Any(source => source.ReceiptId == receipt.ReceiptId) &&
            contributors.All(source => sourceIds.Contains(source.ReceiptId, StringComparer.Ordinal))
            ? receipt : null;
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
