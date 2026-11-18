namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class AcceptanceBalancedLaneExpander
{
    internal static bool IsConstrained(AcceptanceTestLane lane) => lane.ExclusiveResourceKeys.Count > 0 ||
        lane.OwnedCollections.Count > 0 || lane.RequiresBuildSystemChange ||
        AcceptanceCheckCommandBuilder.TranslateResolvedLaneFilter(lane.Filter).Contains("--filter-not-trait");

    internal static AcceptanceBalancedLaneExpansion Expand(IReadOnlyList<AcceptanceTestLane> lanes,
        IReadOnlyList<AcceptanceTestClassDescriptor> classes, IReadOnlyDictionary<string, double> medians,
        AcceptanceBalancedLanePoolSettings settings,
        IReadOnlyDictionary<string, AcceptanceLaneWallModel.Input>? modelInputs = null)
    {
        var (handLanes, pool, constraints) = Partition(lanes, classes, settings);
        var isolated = (settings.IsolationConstraints ?? []).SelectMany(constraint =>
            AcceptanceBalancedLanePacker.Pack(constraints[constraint.Name], medians, new AcceptanceBalancedLanePoolSettings
            {
                LaneCount = constraint.LaneCount,
                LaneNamePrefix = constraint.Name,
                ResidualLaneName = settings.ResidualLaneName,
                HistoryWindow = settings.HistoryWindow,
                DefaultClassSecondsWhenNoHistory = settings.DefaultClassSecondsWhenNoHistory
            }, modelInputs).Lanes.Select(lane => CreateMemberLane(lane.Name, lane.Members, settings,
                constraint.ExclusiveResourceKeys, lane.PredictedSeconds)));
        var layout = AcceptanceBalancedLanePacker.Pack(pool, medians, settings, modelInputs);
        var balanced = layout.Lanes.Select(lane => CreateMemberLane(lane.Name, lane.Members, settings,
            settings.ExclusiveResourceKeys, lane.PredictedSeconds));
        return new([.. handLanes, .. isolated, .. balanced, CreateResidualLane(classes, settings)], layout);
    }

    internal static (AcceptanceTestLane[] HandLanes, List<AcceptanceTestClassDescriptor> Pool,
        Dictionary<string, List<AcceptanceTestClassDescriptor>> Constraints) Partition(
        IReadOnlyList<AcceptanceTestLane> lanes, IReadOnlyList<AcceptanceTestClassDescriptor> classes,
        AcceptanceBalancedLanePoolSettings settings)
    {
        settings.Validate(lanes);
        var constrained = AcceptanceLaneMembership.ResolveOwnedCollections(lanes, classes)
            .Where(lane => IsConstrained(lane) &&
                !settings.PoolEligibleLaneNames.Contains(lane.Name, StringComparer.OrdinalIgnoreCase)).ToArray();
        var pool = new List<AcceptanceTestClassDescriptor>();
        var constraints = (settings.IsolationConstraints ?? []).ToDictionary(constraint => constraint.Name,
            _ => new List<AcceptanceTestClassDescriptor>(), StringComparer.Ordinal);
        foreach (var item in classes.DistinctBy(item => item.FullName).OrderBy(item => item.FullName, StringComparer.Ordinal))
        {
            var owners = AcceptanceLaneMembership.LanesIncluding(constrained, item.FullName);
            if (item.Constraint is { } name)
            {
                if (!constraints.TryGetValue(name, out var members))
                    throw new InvalidDataException($"Test class '{item.FullName}' declares unconfigured isolation constraint '{name}'.");
                if (owners.Count > 0)
                    throw new InvalidDataException($"Test class '{item.FullName}' declares isolation constraint '{name}' and belongs to hand-written constrained lane '{owners[0].Name}'.");
                members.Add(item);
                continue;
            }
            if (owners.Count > 1) throw new InvalidDataException($"Test class '{item.FullName}' has multiple constrained lanes.");
            if (owners.Count == 0) pool.Add(item);
        }
        return (constrained, pool, constraints);
    }

    internal static AcceptanceTestLane CreateResidualLane(IReadOnlyList<AcceptanceTestClassDescriptor> classes,
        AcceptanceBalancedLanePoolSettings settings)
    {
        // Complement the scanner, not hand-maintained fragments: every missed class runs,
        // including a class missed inside a constrained lane's explicit exclusions.
        var residual = string.Join('&', classes.Select(item => "FullyQualifiedName!=" + item.FullName)
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));
        // A sentinel exclusion selects everything when there are no scanned or constrained classes.
        if (residual.Length == 0) residual = "FullyQualifiedName!=Mcg.NoScannedInfrastructureClasses";
        return CreateLane(settings.ResidualLaneName, residual, settings, settings.ExclusiveResourceKeys) with
            { AllowsEmptySelection = true };
    }

    internal static AcceptanceTestLane CreateMemberLane(string name, IEnumerable<string> members,
        AcceptanceBalancedLanePoolSettings settings, IReadOnlyList<string> exclusiveResourceKeys, double predictedSeconds = 0) =>
        CreateLane(name, string.Join('|', members.Select(member => "FullyQualifiedName=" + member)),
            settings, exclusiveResourceKeys, predictedSeconds);

    private static AcceptanceTestLane CreateLane(string name, string filter,
        AcceptanceBalancedLanePoolSettings settings, IReadOnlyList<string> exclusiveResourceKeys, double predictedSeconds = 0) =>
        new(name, settings.TraitExclusions.Count == 0 ? filter :
            $"({filter})&{string.Join('&', settings.TraitExclusions)}", predictedSeconds)
        { ExclusiveResourceKeys = exclusiveResourceKeys };
}
