using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: immutable manifest/source/history reads; no shared state is changed.
public sealed class RemainderPoolPlacementTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ShippedManifest_RemainderClassesHaveOnePoolOrResidualLane(bool readHistory)
    {
        var root = InfrastructureTestSupport.FindRepositoryRoot();
        var settings = AcceptanceGateEngineSettings.Load(root);
        var pool = Assert.IsType<AcceptanceBalancedLanePoolSettings>(settings.BalancedLanePool);
        var classes = AcceptanceTestClassSourceScanner.Scan(root);
        Assert.NotEmpty(classes);
        Assert.Equal(["Remainder"], pool.PoolEligibleLaneNames);
        Assert.Equal(8, pool.LaneCount);
        var expansion = readHistory
            ? AcceptanceInfrastructureLaneResolver.ResolveExecution(settings, root)
            : AcceptanceBalancedLaneExpander.Expand(settings.InfrastructureTestLanes, classes,
                new Dictionary<string, double>(), pool);

        Assert.DoesNotContain(expansion.Lanes, lane => lane.Name == "Remainder");
        var poolNames = Enumerable.Range(1, 8).Select(index => $"{pool.LaneNamePrefix} {index}").ToArray();
        Assert.Equal(poolNames, expansion.Layout!.Lanes.Select(lane => lane.Name));
        foreach (var name in poolNames.Append(pool.ResidualLaneName))
            AssertHostIntegrationExcluded(Assert.Single(expansion.Lanes, lane => lane.Name == name).Filter);

        var remainder = Assert.Single(settings.InfrastructureTestLanes, lane => lane.Name == "Remainder");
        var members = classes.Where(item =>
            AcceptanceLaneMembership.LanesIncluding([remainder], item.FullName).Count > 0).ToArray();
        Assert.NotEmpty(members);
        foreach (var item in members)
        {
            var owner = Assert.Single(AcceptanceLaneMembership.LanesIncluding(expansion.Lanes, item.FullName));
            Assert.Contains(owner.Name, poolNames.Append(pool.ResidualLaneName));
        }
        // A class absent from the scanner still belongs to the unchanged complement lane.
        const string missedClass = "Synthetic.ScannerMissedInfrastructureTests";
        Assert.DoesNotContain(classes, item => item.FullName == missedClass);
        Assert.Equal(pool.ResidualLaneName,
            Assert.Single(AcceptanceLaneMembership.LanesIncluding(expansion.Lanes, missedClass)).Name);

        string[] protectedNames = ["Isolated process spawning A", "Isolated process spawning B",
            "Remainder merge train and ratchet"];
        foreach (var name in protectedNames)
        {
            var original = Assert.Single(settings.InfrastructureTestLanes, lane => lane.Name == name);
            Assert.Equal(original, Assert.Single(expansion.Lanes, lane => lane.Name == name));
        }
        var otherConstraints = AcceptanceLaneMembership.ResolveOwnedCollections(settings.InfrastructureTestLanes, classes)
            .Where(lane => lane.Name != "Remainder" && AcceptanceBalancedLaneExpander.IsConstrained(lane)).ToArray();
        Assert.Equal(otherConstraints, expansion.Lanes.Take(otherConstraints.Length));
        Assert.Equal(otherConstraints.Length + 8 + 1, expansion.Lanes.Count);
    }

    [Fact]
    public void LocalPartition_RemainderPreservesCoverageAndHostExclusion()
    {
        var root = InfrastructureTestSupport.FindRepositoryRoot();
        var settings = AcceptanceGateEngineSettings.Load(root);
        var names = ReadRemainderPartitionNames(root);
        string[] requiredNames = ["Isolated process spawning A", "Isolated process spawning B",
            "Remainder merge train and ratchet", "Remainder"];
        foreach (var name in requiredNames)
            Assert.Contains(name, names);
        Assert.Empty(names.GroupBy(name => name, StringComparer.Ordinal).Where(group => group.Count() > 1));

        // MtpTestRunner resolves these raw filters, independently of balancedLanePool.
        var lanes = ResolvePartitionLanes(settings, names);
        AssertRemainderCoverage(RemainderTargets(root, settings), lanes);
        AssertHostIntegrationExcluded(Assert.Single(lanes, lane => lane.Name == "Remainder").Filter);
    }

    [Fact]
    public void LocalPartition_RemovingSoleCoveringLane_ReportsUncoveredClass()
    {
        var root = InfrastructureTestSupport.FindRepositoryRoot();
        var settings = AcceptanceGateEngineSettings.Load(root);
        var lanes = ResolvePartitionLanes(settings, ReadRemainderPartitionNames(root));
        var targets = RemainderTargets(root, settings);
        AssertRemainderCoverage(targets, lanes);
        var witness = targets.FirstOrDefault(name => AcceptanceLaneMembership.LanesIncluding(lanes, name).Count == 1);
        Assert.True(witness is not null, "No scanned Remainder class has a sole covering partition lane.");
        var owner = Assert.Single(AcceptanceLaneMembership.LanesIncluding(lanes, witness!));
        var reduced = lanes.Where(lane => lane.Name != owner.Name).ToArray();

        // Exercise coverage independently: removing a required lane must fail for the uncovered class.
        var failure = Assert.Throws<Xunit.Sdk.EmptyException>(() => AssertRemainderCoverage(targets, reduced));
        Assert.Contains(witness!, failure.Message);
    }

    [Fact]
    public void LocalPartition_ComplementWithoutHostExclusion_IsRejected()
    {
        var root = InfrastructureTestSupport.FindRepositoryRoot();
        var settings = AcceptanceGateEngineSettings.Load(root);
        var remainder = Assert.Single(settings.InfrastructureTestLanes, lane => lane.Name == "Remainder");
        AssertHostIntegrationExcluded(remainder.Filter);
        var candidate = remainder with
        {
            Filter = string.Join('&', remainder.Filter.Split('&').Where(term =>
                !term.Trim().Equals("Category!=HostIntegration", StringComparison.OrdinalIgnoreCase)))
        };

        Assert.NotEqual(remainder.Filter, candidate.Filter);
        Assert.Throws<Xunit.Sdk.TrueException>(() => AssertHostIntegrationExcluded(candidate.Filter));
    }

    [Fact]
    public void ResolveIdentity_OnlyLaneCountChanges_KeepTheSameCoverage()
    {
        var root = InfrastructureTestSupport.FindRepositoryRoot();
        var settings = AcceptanceGateEngineSettings.Load(root);
        var copy = JsonSerializer.SerializeToNode(settings)!;
        copy[nameof(AcceptanceGateEngineSettings.BalancedLanePool)]![nameof(AcceptanceBalancedLanePoolSettings.LaneCount)] = 4;
        var four = copy.Deserialize<AcceptanceGateEngineSettings>()!;
        Assert.Equal(8, settings.BalancedLanePool!.LaneCount);
        Assert.Equal(4, four.BalancedLanePool!.LaneCount);

        var identity = AcceptanceInfrastructureLaneResolver.ResolveIdentity(settings, root);
        var smallerIdentity = AcceptanceInfrastructureLaneResolver.ResolveIdentity(four, root);
        Assert.Equal(JsonSerializer.Serialize(identity.Lanes), JsonSerializer.Serialize(smallerIdentity.Lanes));
        Assert.Null(identity.Layout);
        Assert.DoesNotContain(identity.Lanes, lane => lane.Name == "Remainder");
        Assert.Single(identity.Lanes, lane => lane.Name.StartsWith(settings.BalancedLanePool.LaneNamePrefix + " ",
            StringComparison.Ordinal));
    }

    private static string[] ReadRemainderPartitionNames(string root)
    {
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "config", "acceptance-manifest.json")));
        var partition = Assert.Single(manifest.RootElement.GetProperty("engine").GetProperty("localTestPartitions").EnumerateArray(),
            item => item.GetProperty("name").GetString() == "Remainder");
        Assert.False(partition.TryGetProperty("additionalFilters", out _));
        return partition.GetProperty("laneNames").EnumerateArray().Select(item => item.GetString()!).ToArray();
    }

    private static AcceptanceTestLane[] ResolvePartitionLanes(AcceptanceGateEngineSettings settings, string[] names) =>
        names.Select(name => Assert.Single(settings.InfrastructureTestLanes, lane => lane.Name == name)).ToArray();

    private static string[] RemainderTargets(string root, AcceptanceGateEngineSettings settings)
    {
        var remainder = Assert.Single(settings.InfrastructureTestLanes, lane => lane.Name == "Remainder");
        var targets = AcceptanceTestClassSourceScanner.Scan(root)
            .Where(item => AcceptanceLaneMembership.LanesIncluding([remainder], item.FullName).Count > 0)
            .Select(item => item.FullName).OrderBy(name => name, StringComparer.Ordinal).ToArray();
        Assert.NotEmpty(targets);
        return targets;
    }

    private static void AssertRemainderCoverage(IReadOnlyList<string> targets, IReadOnlyList<AcceptanceTestLane> lanes)
    {
        var uncovered = targets.Where(name => AcceptanceLaneMembership.LanesIncluding(lanes, name).Count == 0).ToArray();
        Assert.Empty(uncovered);
    }

    private static void AssertHostIntegrationExcluded(string filter) =>
        Assert.True(AcceptanceCheckCommandBuilder.HasMtpTraitExclusion(
            AcceptanceCheckCommandBuilder.TranslateResolvedLaneFilter(filter).ToArray(), "Category=HostIntegration"));
}
