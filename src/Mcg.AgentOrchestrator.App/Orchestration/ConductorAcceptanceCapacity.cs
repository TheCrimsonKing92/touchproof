namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class ConductorAcceptanceCapacity
{
    internal static ConductorAcceptanceCapacitySnapshot CaptureActive(
        IEnumerable<KeyValuePair<string, CohortGateRun>> cohortGateRuns)
    {
        var activeRoots = cohortGateRuns
            .Where(pair => !pair.Value.Completion.Task.IsCompleted)
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => new ConductorAcceptanceCapacityRoot(
                pair.Key,
                pair.Value.MemberGoalIds,
                pair.Value.StartedAt))
            .ToArray();
        return new ConductorAcceptanceCapacitySnapshot(activeRoots);
    }

    internal static IEnumerable<string> GetUnsettledGroupedGateIdentities(
        IEnumerable<KeyValuePair<string, CohortGateRun>> cohortGateRuns,
        ConductorGroupedGateAttemptCoordinator? groupedGateAttempts,
        string? onlyGoalId)
    {
        var tracked = cohortGateRuns
            .Where(pair => onlyGoalId is null || pair.Value.MemberGoalIds.Contains(onlyGoalId))
            .Select(pair => pair.Value.AttemptMetadataPath is { } path
                ? $"gate-grouped:{Path.GetFileName(path)}"
                : $"gate-cohort:{pair.Key}:{pair.Value.StartedAt.UtcTicks}");
        var persisted = (groupedGateAttempts?.ReadOpen() ?? [])
            .Where(attempt => onlyGoalId is null || attempt.Members.Any(member => member.GoalId == onlyGoalId))
            .Select(attempt => $"gate-grouped:{Path.GetFileName(attempt.MetadataPath)}");
        return tracked.Concat(persisted);
    }
}
