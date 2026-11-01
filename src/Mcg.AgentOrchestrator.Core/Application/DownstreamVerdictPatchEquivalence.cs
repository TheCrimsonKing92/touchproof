namespace Mcg.AgentOrchestrator.Core;

internal static class DownstreamVerdictPatchEquivalence
{
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
