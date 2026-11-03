using System.Reflection;
using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: reads the checked-in manifest and modifies only in-memory lane copies.
public sealed class RemainderBalanceDLaneMoveTests
{
    private const string Remainder = "Remainder";
    private const string BalanceA = "Remainder balance A";
    private const string BalanceC = "Remainder balance C";
    private const string BalanceD = "Remainder balance D";
    private const string SharedKey = "machine:GitHeavyLanes";
    private static readonly string[] MovedClassNames =
    [
        "MergeTrainFocusedRecheckReceiptAcceptanceTests",
        "MergeTrainReproductionAttributionTests",
        "PassedCohortReceiptApprovalHoldTests",
        "IntegrationMergeRatchetOnlyCleanTests",
        "MergeTrainFocusedRecheckTests"
    ];

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LoadedManifest_SelectsEveryMovedClassOnlyInBalanceD(bool resolveCollections)
    {
        var lanes = LoadLanes();
        var types = RunnableTypes();
        if (resolveCollections) lanes = Resolve(lanes, types);

        Assert.Equal(28, lanes.Count);
        foreach (var name in MovedClassNames)
        {
            var type = Assert.Single(types, type => type.Name == name);
            Assert.Equal(new[] { BalanceD }, LaneNames(lanes, type));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BeforeMoveComparison_ChangesOnlyTheFiveMovedClasses(bool resolveCollections)
    {
        var loaded = LoadLanes();
        var beforeMove = BeforeMove(loaded);
        var types = RunnableTypes();
        var moved = MovedClassNames.Select(name => Assert.Single(types, type => type.Name == name)).ToHashSet();
        if (resolveCollections)
        {
            loaded = Resolve(loaded, types);
            beforeMove = Resolve(beforeMove, types);
        }

        Assert.Equal(28, loaded.Count);
        Assert.Equal(27, beforeMove.Count);
        foreach (var type in moved)
        {
            Assert.Equal(new[] { Remainder }, LaneNames(beforeMove, type));
            Assert.Equal(new[] { BalanceD }, LaneNames(loaded, type));
        }
        foreach (var type in types.Where(type => !moved.Contains(type)))
            Assert.Equal(LaneNames(beforeMove, type), LaneNames(loaded, type));
    }

    [Fact]
    public void NegativeControls_DetectDoubleAndMissingMembership()
    {
        var loaded = LoadLanes();
        var types = RunnableTypes();
        var moved = Assert.Single(types, type => type.Name == "MergeTrainFocusedRecheckTests");
        var coupled = Assert.Single(types,
            type => type.Name == "MergeTrainFocusedRecheckReceiptAcceptanceTests");
        const string exclusion = "&FullyQualifiedName!~MergeTrainFocusedRecheckTests";
        const string inclusion = "|FullyQualifiedName~MergeTrainFocusedRecheckTests";
        var remainder = Assert.Single(loaded, lane => lane.Name == Remainder);
        var balanceD = Assert.Single(loaded, lane => lane.Name == BalanceD);
        Assert.Equal(2, remainder.Filter.Split(exclusion, StringSplitOptions.None).Length);
        Assert.Equal(2, balanceD.Filter.Split(inclusion, StringSplitOptions.None).Length);

        var doubleMembership = loaded.Select(lane => lane.Name == Remainder
            ? lane with { Filter = lane.Filter.Replace(exclusion, "", StringComparison.Ordinal) }
            : lane).ToArray();
        Assert.Equal(new[] { Remainder, BalanceD }, LaneNames(doubleMembership, moved));
        Assert.Equal(new[] { BalanceD }, LaneNames(doubleMembership, coupled));

        var missingMembership = loaded.Select(lane => lane.Name == BalanceD
            ? lane with { Filter = lane.Filter.Replace(inclusion, "", StringComparison.Ordinal) }
            : lane).ToArray();
        Assert.Empty(LaneNames(missingMembership, moved));
        Assert.Equal(new[] { BalanceD }, LaneNames(missingMembership, coupled));
    }

    [Fact]
    public void ManifestShape_NewLaneMatchesBalanceASettingsAndKeepsItsKey()
    {
        var lanes = LoadLanes();
        var balanceA = Assert.Single(lanes, lane => lane.Name == BalanceA);
        var balanceD = Assert.Single(lanes, lane => lane.Name == BalanceD);
        var names = lanes.Select(lane => lane.Name).ToArray();
        Assert.Equal(Array.IndexOf(names, BalanceC) + 1, Array.IndexOf(names, BalanceD));
        Assert.Equal(555.8, balanceD.EstimatedSerialSeconds);
        Assert.Empty(balanceD.ExclusiveResourceKeys);
        Assert.Equal(balanceA.ExclusiveResourceKeys, balanceD.ExclusiveResourceKeys);
        Assert.Empty(balanceD.OwnedCollections);
        Assert.Equal(balanceA.OwnedCollections, balanceD.OwnedCollections);
        Assert.False(balanceD.RequiresBuildSystemChange);
        Assert.Equal(balanceA.RequiresBuildSystemChange, balanceD.RequiresBuildSystemChange);
        Assert.False(AcceptanceBalancedLaneExpander.IsConstrained(balanceD));
        Assert.Equal(415.6, Assert.Single(lanes, lane => lane.Name == Remainder).EstimatedSerialSeconds);
        Assert.Equal(string.Join("|", MovedClassNames.Select(name => $"FullyQualifiedName~{name}")),
            balanceD.Filter);

        string[] machineLocalKeys = ["xunit:EnvMutation", "xunit:ProcessSpawningProcessLocal",
            "xunit:ProcessSpawning", "xunit:DotnetBuildSlots"];
        string[][] keySets = [machineLocalKeys, [.. machineLocalKeys, SharedKey]];
        foreach (var keys in keySets)
            Assert.Equal(RemoteLaneResourceEligibility.ExclusionReason(
                    balanceA.ExclusiveResourceKeys, balanceA.RequiresBuildSystemChange, keys),
                RemoteLaneResourceEligibility.ExclusionReason(
                    balanceD.ExclusiveResourceKeys, balanceD.RequiresBuildSystemChange, keys));
    }

    [Fact]
    public void ManifestShape_PartitionAndPropertiesPreserveTheBalanceAPattern()
    {
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            InfrastructureTestSupport.FindRepositoryRoot(), "config", "acceptance-manifest.json")));
        var engine = manifest.RootElement.GetProperty("engine");
        var lanes = engine.GetProperty("infrastructureTestLanes").EnumerateArray().ToArray();
        var balanceA = Assert.Single(lanes, lane => lane.GetProperty("name").GetString() == BalanceA);
        var balanceD = Assert.Single(lanes, lane => lane.GetProperty("name").GetString() == BalanceD);
        Assert.Equal(balanceA.EnumerateObject().Select(property => property.Name).Order(),
            balanceD.EnumerateObject().Select(property => property.Name).Order());
        foreach (var property in balanceA.EnumerateObject().Where(property =>
                     property.Name is not ("name" or "filter" or "estimatedSerialSeconds")))
            Assert.Equal(property.Value.GetRawText(), balanceD.GetProperty(property.Name).GetRawText());

        var partition = Assert.Single(engine.GetProperty("localTestPartitions").EnumerateArray(),
            partition => partition.GetProperty("name").GetString() == Remainder);
        var partitionNames = partition.GetProperty("laneNames").EnumerateArray()
            .Select(name => name.GetString()).ToArray();
        Assert.Contains(BalanceD, partitionNames);
        Assert.Equal(Array.IndexOf(partitionNames, BalanceC) + 1, Array.IndexOf(partitionNames, BalanceD));
        var comment = engine.GetProperty("laneEstimateComment").GetString()!;
        Assert.All(MovedClassNames, name => Assert.Contains(name, comment));
        Assert.Contains("67ae3620-0-20261102135022677", comment);
        Assert.Contains("892a4f8e-gate-20261102205239177 (rerun)", comment);
    }

    [Fact]
    public void ExpandedManifest_EveryDiscoveredClassHasExactlyOneLane()
    {
        var root = InfrastructureTestSupport.FindRepositoryRoot();
        var settings = AcceptanceGateEngineSettings.Load(root);
        var classes = AcceptanceTestClassSourceScanner.Scan(root);
        Assert.NotEmpty(classes);
        Assert.NotNull(settings.BalancedLanePool);
        var expanded = AcceptanceBalancedLaneExpander.Expand(settings.InfrastructureTestLanes, classes,
            new Dictionary<string, double>(), settings.BalancedLanePool);
        foreach (var item in classes)
        {
            var matches = AcceptanceLaneMembership.LanesIncluding(expanded.Lanes, item.FullName);
            Assert.True(matches.Count == 1, item.FullName + ": " + string.Join(',', matches.Select(lane => lane.Name)));
        }
        foreach (var name in MovedClassNames)
        {
            var moved = Assert.Single(classes, item => item.FullName == name);
            Assert.StartsWith(settings.BalancedLanePool.LaneNamePrefix,
                Assert.Single(AcceptanceLaneMembership.LanesIncluding(expanded.Lanes, moved.FullName)).Name);
        }
    }

    private static IReadOnlyList<AcceptanceTestLane> BeforeMove(IReadOnlyList<AcceptanceTestLane> lanes)
    {
        Assert.Single(lanes, lane => lane.Name == BalanceD);
        var remainder = Assert.Single(lanes, lane => lane.Name == Remainder);
        var exclusions = string.Concat(MovedClassNames.Select(name => $"&FullyQualifiedName!~{name}"));
        Assert.Equal(2, remainder.Filter.Split(exclusions, StringSplitOptions.None).Length);
        return lanes.Where(lane => lane.Name != BalanceD).Select(lane => lane.Name == Remainder
            ? lane with { Filter = lane.Filter.Replace(exclusions, "", StringComparison.Ordinal) }
            : lane).ToArray();
    }

    private static IReadOnlyList<AcceptanceTestLane> LoadLanes() =>
        AcceptanceGateEngineSettings.Load(InfrastructureTestSupport.FindRepositoryRoot()).InfrastructureTestLanes;

    private static Type[] RunnableTypes() =>
        typeof(RemainderBalanceDLaneMoveTests).Assembly.GetTypes()
            .Where(type => type is { IsClass: true, IsAbstract: false } &&
                type.GetMethods(BindingFlags.Instance | BindingFlags.Public)
                    .Any(method => method.GetCustomAttributes(inherit: true)
                        .Any(attribute => attribute is Xunit.FactAttribute)))
            .OrderBy(type => type.FullName, StringComparer.Ordinal)
            .ToArray();

    private static IReadOnlyList<AcceptanceTestLane> Resolve(
        IReadOnlyList<AcceptanceTestLane> lanes, Type[] types) =>
        AcceptanceLaneMembership.ResolveOwnedCollections(lanes, types.Select(type =>
            new AcceptanceTestClassDescriptor(type.FullName ?? type.Name,
                type.GetCustomAttribute<Xunit.CollectionAttribute>(inherit: true)?.Name)).ToArray());

    private static string[] LaneNames(IReadOnlyList<AcceptanceTestLane> lanes, Type type) =>
        AcceptanceLaneMembership.LanesIncluding(lanes, type.FullName!)
            .Select(lane => lane.Name).OrderBy(name => name, StringComparer.Ordinal).ToArray();
}
