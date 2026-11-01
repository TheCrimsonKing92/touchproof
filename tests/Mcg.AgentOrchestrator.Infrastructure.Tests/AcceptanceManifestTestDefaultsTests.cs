using System.Text.Json.Nodes;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each test owns its fixture files and only reads tracked settings.
public sealed class AcceptanceManifestTestDefaultsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "manifest-defaults-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void DefaultEngine_PreservesHandWrittenPartitionNamesAndFilters()
    {
        var manifest = AcceptanceManifestTestDefaults.WithEngine("""{"version":1,"checks":[]}""");
        var settings = AcceptanceGateEngineSettings.Parse(manifest);
        var tracked = AcceptanceGateEngineSettings.Load(InfrastructureTestSupport.FindRepositoryRoot());

        Assert.NotNull(tracked.BalancedLanePool);
        Assert.Null(settings.BalancedLanePool);
        Assert.NotEmpty(settings.InfrastructureTestLanes);
        var expected = tracked.InfrastructureTestLanes.Select(lane => (lane.Name, lane.Filter)).ToArray();
        Assert.Equal(expected, settings.InfrastructureTestLanes.Select(lane => (lane.Name, lane.Filter)));

        // These source-free fixtures still execute every declared partition, rather than
        // dissolving unconstrained names and adding a scanner-complement residual.
        Directory.CreateDirectory(Path.Combine(_root, "config"));
        File.WriteAllText(Path.Combine(_root, "config", "acceptance-manifest.json"), manifest);
        Assert.Empty(AcceptanceTestClassSourceScanner.Scan(_root));
        var execution = AcceptanceInfrastructureLaneResolver.ResolveExecution(settings, _root);
        Assert.Null(execution.Layout);
        Assert.Equal(expected, execution.Lanes.Select(lane => (lane.Name, lane.Filter)));
    }

    [Fact]
    public void ExplicitEngine_PreservesPoolAndBalancesDiscoveredClasses()
    {
        const string manifest = """
            {"version":1,"checks":[],"engine":{
              "laneTestSourceRoots":["tests/Mcg.AgentOrchestrator.Infrastructure.Tests"],
              "infrastructureTestLanes":[],
              "balancedLanePool":{"laneCount":2,"historyWindow":2}
            }}
            """;
        var result = AcceptanceManifestTestDefaults.WithEngine(manifest);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(manifest), JsonNode.Parse(result)));
        var settings = AcceptanceGateEngineSettings.Parse(result);
        Assert.NotNull(settings.BalancedLanePool);
        Directory.CreateDirectory(Path.Combine(_root, "config"));
        File.WriteAllText(Path.Combine(_root, "config", "acceptance-manifest.json"), result);
        var source = Path.Combine(_root, "tests", "Mcg.AgentOrchestrator.Infrastructure.Tests");
        Directory.CreateDirectory(source);
        foreach (var name in new[] { "FirstTests", "SecondTests" })
            File.WriteAllText(Path.Combine(source, name + ".cs"),
                $"public class {name} {{ [Xunit.Fact] public void Executes() {{ }} }}");
        Assert.Equal(["FirstTests", "SecondTests"],
            AcceptanceTestClassSourceScanner.Scan(_root).Select(item => item.FullName));

        var execution = AcceptanceInfrastructureLaneResolver.ResolveExecution(settings, _root);
        Assert.NotNull(execution.Layout);
        Assert.Equal(2, execution.Layout.Lanes.Count);
        Assert.Equal(["FirstTests", "SecondTests"],
            execution.Layout.Lanes.SelectMany(lane => lane.Members).Order(StringComparer.Ordinal));
        var residual = Assert.Single(execution.Lanes,
            lane => lane.Name == settings.BalancedLanePool.ResidualLaneName);
        Assert.True(residual.AllowsEmptySelection);
        Assert.Equal(3, execution.Lanes.Count);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
