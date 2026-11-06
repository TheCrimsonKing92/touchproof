namespace Mcg.AgentOrchestrator.Core;

internal static class ReviewFindingRoundSalvage
{
    // Commit-independent salvage is safe only when every rejected transition remains advisory.
    internal static bool IsAdvisoryOnlyIdentityMove(
        IReadOnlyList<ReviewFinding> previous,
        ReviewFindingRound round,
        ReviewFindingContractViolation violation) =>
        violation.Code == ReviewFindingConvergence.IdentityMovedViolationCode &&
        violation.IdentityMismatches is { Count: > 0 } mismatches &&
        mismatches.All(mismatch =>
            mismatch.Code == ReviewFindingConvergence.IdentityMovedViolationCode &&
            string.Equals(mismatch.PriorStableId, mismatch.SubmittedStableId, StringComparison.Ordinal) &&
            previous.Any(finding =>
                finding.StableId == mismatch.PriorStableId &&
                finding is { State: ReviewFindingState.Open, Severity: FindingSeverity.Advisory }) &&
            round.Findings.Any(finding =>
                finding.StableId == mismatch.SubmittedStableId &&
                finding is { State: ReviewFindingState.Open, Severity: FindingSeverity.Advisory }));

    internal static IReadOnlyList<ReviewFinding> ApplyIdentityRejectedRound(
        IReadOnlyList<ReviewFinding> previous,
        ReviewFindingRound round,
        ReviewFindingContractViolation identityViolation,
        AgentRole role,
        ReviewRetryCapReceipt? reviewRetryCap,
        IReadOnlyList<EffectiveAcceptanceCriteriaCorrection> corrections,
        string? reviewedCommit,
        IReadOnlyList<FindingEvidenceReceipt> evidenceReceipts,
        out ReviewFindingContractViolation violation)
    {
        var acceptedRound = round with
        {
            Findings = round.Findings.Where(finding =>
                !ReviewFindingConvergence.IsRejectedIdentityTransition(identityViolation, finding.StableId)).ToArray()
        };
        violation = identityViolation;
        try
        {
            // Salvage must enforce the same resolution policy as a round with no identity violation.
            if (role == AgentRole.Reviewer && reviewRetryCap is { IsAtCap: true })
                ReviewFindingConvergence.ValidateResolutionAtCap(
                    previous, acceptedRound, corrections, reviewedCommit, evidenceReceipts);
            else if (role == AgentRole.Reviewer && reviewRetryCap is null)
                ReviewFindingConvergence.ValidateResolutionWithoutCapReceipt(
                    previous, acceptedRound, corrections, reviewedCommit, evidenceReceipts);
        }
        catch (ReviewFindingConvergenceException ex) when (
            ReviewFindingConvergence.IsRejectedCapResolutionRound(ex.Violation))
        {
            // Carry both rejection sets so history replay cannot accept either invalid transition.
            violation = ex.Violation with
            {
                IdentityMismatches = (ex.Violation.IdentityMismatches ?? [])
                    .Concat(identityViolation.IdentityMismatches ?? []).ToArray()
            };
            return ReviewFindingConvergence.ApplyRejectedCapResolutionRound(previous, round, violation);
        }

        return ReviewFindingConvergence.ApplyRound(previous, acceptedRound);
    }

    internal static IReadOnlyList<ReviewFinding> FindHiddenSubmittedBlockers(
        ReviewFindingRound round,
        IReadOnlyList<ReviewFinding> ledger,
        IReadOnlyList<EffectiveAcceptanceCriteriaCorrection> corrections) =>
        ReviewFindings.GetOpenBlockingFindings(round.Findings, corrections)
            .Where(finding => ReviewFindingConvergence.ResolveMergedFinding(ledger, round, finding.StableId)
                is not { State: ReviewFindingState.Open, Severity: FindingSeverity.Blocking })
            .ToArray();

    internal static IReadOnlyList<ReviewFinding>? SelectRejectedLedger(
        ReviewFindingContractViolation? violation,
        bool identityTransitionSalvaged,
        ReviewFindingRound round,
        IReadOnlyList<ReviewFinding> ledger,
        IReadOnlyList<EffectiveAcceptanceCriteriaCorrection> corrections) =>
        // Retaining an older advisory must not certify that the submitted blocker reached the ledger.
        violation is not null &&
        (ReviewFindingConvergence.IsRejectedCapResolutionRound(violation) ||
         violation.Code == ReviewFindingConvergence.OmittedOpenFindingViolationCode ||
         violation.Code == ReviewFindingConvergence.FindingFormatViolationCode ||
         identityTransitionSalvaged) &&
        FindHiddenSubmittedBlockers(round, ledger, corrections).Count == 0
            ? ledger
            : null;
}
