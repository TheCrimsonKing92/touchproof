namespace Mcg.AgentOrchestrator.App.Orchestration;

public sealed record DecisionSnapshotComparison(
    string? InputError,
    IReadOnlyList<DecisionSnapshotDifference> Differences)
{
    public bool Passed => InputError is null &&
        Differences.All(difference => !difference.IsRegression || difference.Exempted);
}
