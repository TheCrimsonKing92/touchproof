namespace Mcg.AgentOrchestrator.Core;

internal static class DownstreamVerdictPatchEquivalence
{
    internal static bool TryPreserveForRetry(
        ICandidatePatchEquivalence? equivalence, Goal goal, TaskSpec downstream,
        TaskSpec retriedTask, string? candidate,
        Func<string?, string?, bool> sameReviewedCommit, out string message) =>
        TryPreserve(equivalence, goal, downstream, candidate, sameReviewedCommit,
            $"Preserved {downstream.RequiredRole} task because its verification covers candidate {candidate?.Trim()} while upstream {retriedTask.RequiredRole} task {retriedTask.Id.Value[..8]} is retried.",
            out message);

    internal static bool TryPreserveForCompletion(
        ICandidatePatchEquivalence? equivalence, Goal goal, TaskSpec downstream,
        TaskSpec retriedTask, string? candidate,
        Func<string?, string?, bool> sameReviewedCommit, out string message) =>
        TryPreserve(equivalence, goal, downstream, candidate, sameReviewedCommit,
            $"Preserved {downstream.RequiredRole} task because retried upstream {retriedTask.RequiredRole} task {retriedTask.Id.Value[..8]} completed with unchanged candidate {candidate?.Trim()}.",
            out message);

    private static bool TryPreserve(
        ICandidatePatchEquivalence? equivalence, Goal goal, TaskSpec downstream,
        string? candidate, Func<string?, string?, bool> sameReviewedCommit,
        string unchangedCandidateMessage, out string message)
    {
        message = string.Empty;
        if (downstream.Status == WorkTaskStatus.Completed &&
            downstream.LastVerification is not null &&
            sameReviewedCommit(downstream.LastVerification.ReviewedCommit, candidate))
        {
            message = unchangedCandidateMessage;
            return true;
        }

        if (!TryCarry(equivalence, goal, downstream, candidate, out var reviewedSha, out var evidence))
            return false;

        downstream.AdvanceVerifiedCandidateCoverage(candidate!);
        message = $"Preserved {downstream.RequiredRole} task because candidate {candidate!.Trim()} is patch-equivalent to reviewed candidate {reviewedSha} ({evidence}).";
        return true;
    }

    internal static bool TryCarry(
        ICandidatePatchEquivalence? equivalence,
        Goal goal,
        TaskSpec downstream,
        string? currentSha,
        out string reviewedSha,
        out string evidence)
    {
        reviewedSha = downstream.LastVerification?.ReviewedCommit?.Trim() ?? string.Empty;
        evidence = string.Empty;
        if (equivalence is null ||
            downstream.Status != WorkTaskStatus.Completed ||
            downstream.RequiredRole is not (AgentRole.Tester or AgentRole.Reviewer) ||
            reviewedSha.Length == 0 || string.IsNullOrWhiteSpace(currentSha))
            return false;

        try
        {
            return equivalence.AreEquivalent(goal.Id, reviewedSha, currentSha.Trim(), out evidence) &&
                !string.IsNullOrWhiteSpace(evidence);
        }
        catch (Exception)
        {
            // A verdict can only carry forward on positive equivalence evidence.
            evidence = string.Empty;
            return false;
        }
    }
}
