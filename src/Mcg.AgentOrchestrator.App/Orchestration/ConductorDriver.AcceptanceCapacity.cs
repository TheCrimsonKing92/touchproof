namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record ConductorAcceptanceCapacityRoot(
    string Key,
    IReadOnlySet<string> MemberGoalIds,
    DateTimeOffset StartedAt = default);

internal sealed record ConductorAcceptanceCapacitySnapshot(
    IReadOnlyList<ConductorAcceptanceCapacityRoot> ActiveRoots)
{
    internal int ActiveRootCount => ActiveRoots.Count;
}

internal sealed partial class ConductorDriver
{
    internal ConductorAcceptanceCapacitySnapshot GetActiveAcceptanceCohortCapacity() =>
        ConductorAcceptanceCapacity.CaptureActive(_cohortGateRuns);

    internal IEnumerable<string> GetUnsettledGroupedGateIdentities(string? onlyGoalId) =>
        ConductorAcceptanceCapacity.GetUnsettledGroupedGateIdentities(_cohortGateRuns, _groupedGateAttempts, onlyGoalId);
}
