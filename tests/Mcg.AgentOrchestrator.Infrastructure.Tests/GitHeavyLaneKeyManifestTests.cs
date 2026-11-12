using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: reads shipped settings without mutating shared state.
public sealed class GitHeavyLaneKeyManifestTests
{
    private const string SharedKey = "machine:GitHeavyLanes";
    private static readonly string[] GitHeavyLaneNames =
    [
        "Remainder",
        "Remainder merge train and ratchet"
    ];

    [Fact]
    public void ShippedManifest_NoLaneDeclaresTheSharedKey()
    {
        var lanes = LoadLanes();
        Assert.Equal(20, lanes.Count);
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
    }

    private static IReadOnlyList<AcceptanceTestLane> LoadLanes() =>
        AcceptanceGateEngineSettings.Load(InfrastructureTestSupport.FindRepositoryRoot()).InfrastructureTestLanes;
}
