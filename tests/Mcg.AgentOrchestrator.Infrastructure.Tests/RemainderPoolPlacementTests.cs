using System.Security.Cryptography;
using System.Text;
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
    public void LocalPartition_RemainderKeepsPreChangeFiltersAndHostExclusion()
    {
        var root = InfrastructureTestSupport.FindRepositoryRoot();
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "config", "acceptance-manifest.json")));
        var engine = manifest.RootElement.GetProperty("engine");
        var partition = Assert.Single(engine.GetProperty("localTestPartitions").EnumerateArray(),
            item => item.GetProperty("name").GetString() == "Remainder");
        var names = partition.GetProperty("laneNames").EnumerateArray().Select(item => item.GetString()!).ToArray();
        Assert.Equal(["Remainder balance A", "Remainder balance B", "Remainder balance C", "Remainder balance D",
            "Isolated process spawning A", "Isolated process spawning B", "Remainder merge train and ratchet", "Remainder"], names);
        Assert.False(partition.TryGetProperty("additionalFilters", out _));
        // MtpTestRunner resolves these raw filters, independently of balancedLanePool.
        var lanes = AcceptanceGateEngineSettings.Load(root).InfrastructureTestLanes;
        var filters = names.Select(name => Assert.Single(lanes, lane => lane.Name == name).Filter).ToArray();
        // SHA-256 of UTF-8, LF-joined partition filters captured at pre-change HEAD 95d379cb4185.
        // Goal c02ea6c7 adds only FullyQualifiedName~WorkerBuildRequestStructuralFailureTests and
        // FullyQualifiedName~WorkerBuildRequestStructuralIntegrationTests to Remainder balance C.
        Assert.Equal("763509EA66ED54C6EC79FAEFBA15FADFD5F214A4C03919373A9E2C894C94E218",
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', filters)))));
        AssertHostIntegrationExcluded(filters[^1]);
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

    private static void AssertHostIntegrationExcluded(string filter) =>
        Assert.True(AcceptanceCheckCommandBuilder.HasMtpTraitExclusion(
            AcceptanceCheckCommandBuilder.TranslateResolvedLaneFilter(filter).ToArray(), "Category=HostIntegration"));
}
