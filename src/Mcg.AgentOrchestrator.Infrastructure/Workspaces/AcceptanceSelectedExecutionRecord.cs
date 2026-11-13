namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed record AcceptanceSelectedExecutionLane(string Name, string Disposition,
    IReadOnlyList<string> SelectedClasses, IReadOnlyList<string> SkippedClasses, string? Filter,
    string? LaneSkipReason = null, string? ClassSkipReason = null);

// Counts describe distinct class identities in the candidate inventory, not test outcomes.
internal sealed record AcceptanceSelectedExecutionRecord(
    string Mode, string Decision, string? Outcome, string? FullGateReason,
    IReadOnlyList<string> FloorLanes, IReadOnlyList<string> GuardClasses,
    IReadOnlyList<AcceptanceSelectedExecutionLane> Lanes,
    int? ExecutedClassCount, int? SkippedClassCount,
    int? ProposedExecutedClassCount, int? ProposedSkippedClassCount,
    double SelectionMilliseconds, bool StructuralCoverageScoped)
{
    internal static AcceptanceSelectedExecutionRecord Disabled { get; } =
        new("Off", "disabled", null, "Selected execution is disabled.", [], [], [], null, null, null, null, 0, false);
}
