using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

// Owns infrastructure lane expansion for measured execution and stable coverage identity.
internal static class AcceptanceInfrastructureLaneResolver
{
    internal static AcceptanceBalancedLaneExpansion ResolveExecution(
        AcceptanceGateEngineSettings settings, string worktreePath)
    {
        if (settings.BalancedLanePool is not { } pool)
            return new(AcceptanceLaneMembership.ResolveOwnedCollections(settings.InfrastructureTestLanes, worktreePath), null);
        var inputs = AcceptanceClassDurationHistoryStore.ReadModelInputs(worktreePath, pool.HistoryWindow, out var failureReason);
        var medians = inputs.ToDictionary(pair => pair.Key, pair => pair.Value.Seconds, StringComparer.Ordinal);
        var expansion = AcceptanceBalancedLaneExpander.Expand(settings.InfrastructureTestLanes,
            AcceptanceTestClassSourceScanner.Scan(worktreePath), medians, pool, inputs);
        return expansion with { Layout = expansion.Layout! with { HistoryReadFailure = failureReason } };
    }

    internal static AcceptanceBalancedLaneExpansion ResolveIdentity(
        AcceptanceGateEngineSettings settings, string worktreePath, SelectionSourceTexts? sourceTexts = null)
    {
        if (settings.BalancedLanePool is not { } pool)
            return sourceTexts is null ? ResolveExecution(settings, worktreePath) : new(
                settings.InfrastructureTestLanes.All(lane => lane.OwnedCollections.Count == 0)
                    ? settings.InfrastructureTestLanes
                    : AcceptanceLaneMembership.ResolveOwnedCollections(settings.InfrastructureTestLanes,
                        AcceptanceTestClassSourceScanner.Scan(worktreePath, sourceTexts)), null);

        // Identity binds each group's coverage before history can affect scheduling.
        var classes = sourceTexts is null ? AcceptanceTestClassSourceScanner.Scan(worktreePath) :
            AcceptanceTestClassSourceScanner.Scan(worktreePath, sourceTexts);
        var (handLanes, members, constraints) = AcceptanceBalancedLaneExpander.Partition(
            settings.InfrastructureTestLanes, classes, pool);
        var lanes = handLanes.ToList();
        foreach (var constraint in pool.IsolationConstraints ?? [])
        {
            var group = constraints[constraint.Name];
            if (group.Count > 0)
                lanes.Add(AcceptanceBalancedLaneExpander.CreateMemberLane($"{constraint.Name} 1",
                    group.Select(item => item.FullName), pool, constraint.ExclusiveResourceKeys));
        }
        if (members.Count > 0)
            lanes.Add(AcceptanceBalancedLaneExpander.CreateMemberLane($"{pool.LaneNamePrefix} 1",
                members.Select(item => item.FullName), pool, pool.ExclusiveResourceKeys));
        lanes.Add(AcceptanceBalancedLaneExpander.CreateResidualLane(classes, pool));
        return new(lanes, null);
    }
}
