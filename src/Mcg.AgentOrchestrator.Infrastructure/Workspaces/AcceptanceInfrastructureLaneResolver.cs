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
        AcceptanceGateEngineSettings settings, string worktreePath)
    {
        if (settings.BalancedLanePool is not { } pool)
            return ResolveExecution(settings, worktreePath);

        // Identity describes the coverage contract, not the measured scheduling layout.
        // Reuse constrained membership and the scanner complement, but flatten the pool
        // so neither duration history nor the number of occupied lanes can change it.
        var expansion = AcceptanceBalancedLaneExpander.Expand(settings.InfrastructureTestLanes,
            AcceptanceTestClassSourceScanner.Scan(worktreePath), new Dictionary<string, double>(), pool);
        var members = expansion.Layout!.Lanes.SelectMany(lane => lane.Members)
            .Order(StringComparer.Ordinal).ToArray();
        AcceptanceTestLane[] identityPool = members.Length == 0 ? [] :
            [AcceptanceBalancedLaneExpander.CreatePoolLane($"{pool.LaneNamePrefix} 1",
                string.Join('|', members.Select(name => "FullyQualifiedName=" + name)), pool)];
        // Expansion orders hand-written constraints first, then generated lanes and residual.
        // Generated pool keys/traits must not turn the measured layout into identity inputs.
        var constrainedCount = expansion.Lanes.Count - expansion.Layout.Lanes.Count - 1;
        return new([.. expansion.Lanes.Take(constrainedCount),
            .. identityPool, expansion.Lanes[^1]], null);
    }
}
