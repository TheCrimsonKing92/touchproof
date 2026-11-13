namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class ConductorDocumentationExclusionAdmission
{
    internal static string? Describe(
        ConductorParallelAcceptanceCandidate candidate,
        IReadOnlyList<ConductorParallelAcceptanceCandidate> activeCandidates,
        int tick)
    {
        foreach (var existing in activeCandidates)
        {
            var excludedPaths = existing.GetDocumentationExclusionEvidence(candidate);
            if (excludedPaths.Count == 0)
            {
                continue;
            }

            var sample = string.Join(",", excludedPaths.Take(3));
            return $"ADMISSION tick={tick} result=admitted reason=documentation-exclusion " +
                $"goal={candidate.GoalPrefix} peer={existing.GoalPrefix} " +
                $"excludedPathCount={excludedPaths.Count} excludedPathSample={sample}";
        }

        return null;
    }
}
