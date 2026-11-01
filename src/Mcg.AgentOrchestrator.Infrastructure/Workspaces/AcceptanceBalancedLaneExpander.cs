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
        // Collection grouping applies to the remaining pool. Existing constrained filters
        // may split a process-local collection across lanes; only OwnedCollections declares
        // a lane owner, and that membership was resolved above.
        var layout = AcceptanceBalancedLanePacker.Pack(pool, medians, settings);
        var balanced = layout.Lanes.Select(lane => CreatePoolLane(lane.Name,
            string.Join('|', lane.Members.Select(name => "FullyQualifiedName=" + name)), settings, lane.PredictedSeconds));
        // Complement the scanner, not hand-maintained fragments: every missed class runs,
        // including a class missed inside a constrained lane's explicit exclusions.
        var residual = string.Join('&', classes.Select(item => "FullyQualifiedName!=" + item.FullName)
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));
        // A sentinel exclusion selects everything when there are no scanned or constrained classes.
        if (residual.Length == 0) residual = "FullyQualifiedName!=Mcg.NoScannedInfrastructureClasses";
        return new([.. constrained, .. balanced, CreatePoolLane(settings.ResidualLaneName, residual, settings) with
            { AllowsEmptySelection = true }], layout);
    }

    internal static AcceptanceTestLane CreatePoolLane(string name, string filter,
        AcceptanceBalancedLanePoolSettings settings, double predictedSeconds = 0) =>
        new(name, settings.TraitExclusions.Count == 0 ? filter :
            $"({filter})&{string.Join('&', settings.TraitExclusions)}", predictedSeconds)
        { ExclusiveResourceKeys = settings.ExclusiveResourceKeys };
}
