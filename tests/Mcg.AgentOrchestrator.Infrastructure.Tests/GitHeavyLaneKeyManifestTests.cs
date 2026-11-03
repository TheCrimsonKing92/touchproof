using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: reads shipped settings without mutating shared state.
public sealed class GitHeavyLaneKeyManifestTests
{
    private const string SharedKey = "machine:GitHeavyLanes";
    private static readonly string[] GitHeavyLaneNames =
    [
        "Remainder",
        "Remainder balance A",
        "Remainder balance D",
        "Remainder merge train and ratchet"
    ];

    [Fact]
    public void ShippedManifest_NoLaneDeclaresTheSharedKey()
    {
        var lanes = LoadLanes();
        Assert.Equal(28, lanes.Count);
        Assert.All(lanes, lane => Assert.DoesNotContain(SharedKey, lane.ExclusiveResourceKeys));
        foreach (var name in GitHeavyLaneNames)
        {
            var lane = Assert.Single(lanes, lane => lane.Name == name);
            Assert.Empty(lane.ExclusiveResourceKeys);
            Assert.Empty(lane.OwnedCollections);
            Assert.False(lane.RequiresBuildSystemChange);
            Assert.Equal(name is "Remainder" or "Remainder merge train and ratchet",
                AcceptanceBalancedLaneExpander.IsConstrained(lane));
        }
        Assert.NotNull(AcceptanceGateEngineSettings.Load(InfrastructureTestSupport.FindRepositoryRoot()).BalancedLanePool);
    }

    [Fact]
    public void RemoteEligibility_FormerGitHeavyLanesNeedNoMachineLocalKey()
    {
        var lanes = LoadLanes();
        string[] shippedMachineLocalKeys =
        [
            "xunit:EnvMutation",
            "xunit:ProcessSpawningProcessLocal",
            "xunit:ProcessSpawning",
            "xunit:DotnetBuildSlots"
        ];
        string[] extendedMachineLocalKeys = [.. shippedMachineLocalKeys, SharedKey];
        foreach (var name in GitHeavyLaneNames)
        {
            var lane = Assert.Single(lanes, lane => lane.Name == name);
            Assert.Null(RemoteLaneResourceEligibility.ExclusionReason(
                lane.ExclusiveResourceKeys, lane.RequiresBuildSystemChange, shippedMachineLocalKeys));
            Assert.Null(RemoteLaneResourceEligibility.ExclusionReason(
                lane.ExclusiveResourceKeys, lane.RequiresBuildSystemChange, extendedMachineLocalKeys));
        }

        var keylessLane = Assert.Single(lanes, lane => lane.Name == "Remainder balance B");
        Assert.Null(RemoteLaneResourceEligibility.ExclusionReason(
            keylessLane.ExclusiveResourceKeys, keylessLane.RequiresBuildSystemChange, shippedMachineLocalKeys));
    }

    [Fact]
    public void ShippedManifest_FormerBalanceAAndDClassesHaveExactlyOneBalancedLane()
    {
        var root = InfrastructureTestSupport.FindRepositoryRoot();
        var settings = AcceptanceGateEngineSettings.Load(root);
        var classes = AcceptanceTestClassSourceScanner.Scan(root);
        Assert.NotNull(settings.BalancedLanePool);
        var expanded = AcceptanceBalancedLaneExpander.Expand(settings.InfrastructureTestLanes, classes,
            new Dictionary<string, double>(), settings.BalancedLanePool);
        foreach (var name in new[] { "Remainder balance A", "Remainder balance D" })
        {
            var originalLane = Assert.Single(settings.InfrastructureTestLanes, lane => lane.Name == name);
            var members = classes.Where(item =>
                AcceptanceLaneMembership.LanesIncluding([originalLane], item.FullName).Count > 0).ToArray();
            Assert.NotEmpty(members);
            foreach (var item in members)
            {
                var lane = Assert.Single(AcceptanceLaneMembership.LanesIncluding(expanded.Lanes, item.FullName));
                Assert.StartsWith(settings.BalancedLanePool.LaneNamePrefix, lane.Name);
            }
        }
    }

    private static IReadOnlyList<AcceptanceTestLane> LoadLanes() =>
        AcceptanceGateEngineSettings.Load(InfrastructureTestSupport.FindRepositoryRoot()).InfrastructureTestLanes;
}
