namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class AcceptanceBalancedLaneExpander
{
    internal static bool IsConstrained(AcceptanceTestLane lane) => lane.ExclusiveResourceKeys.Count > 0 ||
        lane.OwnedCollections.Count > 0 || lane.RequiresBuildSystemChange ||
        AcceptanceCheckCommandBuilder.TranslateResolvedLaneFilter(lane.Filter).Contains("--filter-not-trait");

    internal static AcceptanceBalancedLaneExpansion Expand(IReadOnlyList<AcceptanceTestLane> lanes,
        IReadOnlyList<AcceptanceTestClassDescriptor> classes, IReadOnlyDictionary<string, double> medians,
        AcceptanceBalancedLanePoolSettings settings)
    {
        settings.Validate(lanes);
        var constrained = AcceptanceLaneMembership.ResolveOwnedCollections(lanes, classes).Where(IsConstrained).ToArray();
        var pool = new List<AcceptanceTestClassDescriptor>();
        foreach (var item in classes.DistinctBy(item => item.FullName).OrderBy(item => item.FullName, StringComparer.Ordinal))
        {
            var owners = AcceptanceLaneMembership.LanesIncluding(constrained, item.FullName);
            if (owners.Count > 1) throw new InvalidDataException($"Test class '{item.FullName}' has multiple constrained lanes.");
            if (owners.Count == 0) pool.Add(item);
        }
        foreach (var group in classes.Where(item => item.Collection is not null).GroupBy(item => item.Collection))
        {
            var owners = group.SelectMany(item => AcceptanceLaneMembership.LanesIncluding(constrained, item.FullName))
                .Select(lane => lane.Name).Distinct(StringComparer.Ordinal).ToArray();
            if (owners.Length > 0 && group.Any(item => pool.Contains(item)))
                throw new InvalidDataException($"Test collection '{group.Key}' needs a single constrained lane owner.");
        }
        var layout = AcceptanceBalancedLanePacker.Pack(pool, medians, settings);
        var balanced = layout.Lanes.Select(lane => new AcceptanceTestLane(lane.Name,
            string.Join('|', lane.Members.Select(name => "FullyQualifiedName=" + name)), lane.PredictedSeconds));
        // Complement the scanner, not hand-maintained fragments: every missed class runs,
        // including a class missed inside a constrained lane's explicit exclusions.
        var residual = string.Join('&', classes.Select(item => "FullyQualifiedName!=" + item.FullName)
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));
        // A sentinel exclusion selects everything when there are no scanned or constrained classes.
        if (residual.Length == 0) residual = "FullyQualifiedName!=Mcg.NoScannedInfrastructureClasses";
        return new([.. constrained, .. balanced, new AcceptanceTestLane(settings.ResidualLaneName, residual)
            { AllowsEmptySelection = true }], layout);
    }
}
