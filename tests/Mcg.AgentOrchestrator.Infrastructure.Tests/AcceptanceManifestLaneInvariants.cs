using Mcg.AgentOrchestrator.Infrastructure;

internal static class AcceptanceManifestLaneInvariants
{
    internal static IReadOnlyList<string> FindViolations(
        IReadOnlyList<AcceptanceTestLane> lanes,
        IReadOnlyList<AcceptanceTestClassDescriptor> classes)
    {
        var violations = new List<string>();
        var collections = classes.Select(item => item.Collection).OfType<string>()
            .ToHashSet(StringComparer.Ordinal);
        foreach (var lane in lanes)
        {
            var matches = AcceptanceLaneMembership.CreateClassMatcher(lane);
            if (!classes.Any(item => matches(item.FullName)))
                violations.Add($"Lane '{lane.Name}' selects no scanned class.");

            foreach (var key in lane.ExclusiveResourceKeys)
            {
                if (key.StartsWith("xunit:", StringComparison.Ordinal) &&
                    !collections.Contains(key["xunit:".Length..]))
                    violations.Add($"Lane '{lane.Name}' resource key '{key}' names no scanned collection.");
            }
        }

        return violations;
    }
}
