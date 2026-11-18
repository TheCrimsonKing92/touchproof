namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed class AcceptanceIsolationConstraintLaneSettings
{
    public string Name { get; init; } = "";
    public int LaneCount { get; init; } = 1;
    public IReadOnlyList<string> ExclusiveResourceKeys { get; init; } = [];
}
