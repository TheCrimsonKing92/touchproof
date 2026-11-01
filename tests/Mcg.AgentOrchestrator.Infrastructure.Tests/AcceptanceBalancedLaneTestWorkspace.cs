using System.Text.Json.Nodes;
using Mcg.AgentOrchestrator.Infrastructure;

// Each caller owns its workspace. Enable the shipped pool explicitly; default
// source-free fixtures retain their hand-written partitions.
internal static class AcceptanceBalancedLaneTestWorkspace
{
    internal static IReadOnlyList<AcceptanceTestLane> EnableShippedPool(string root)
    {
        var path = Path.Combine(root, "config", "acceptance-manifest.json");
        var manifest = JsonNode.Parse(File.ReadAllText(path))!;
        var trackedPath = Path.Combine(InfrastructureTestSupport.FindRepositoryRoot(), "config", "acceptance-manifest.json");
        var tracked = JsonNode.Parse(File.ReadAllText(trackedPath))!;
        manifest["engine"]!["balancedLanePool"] = tracked["engine"]!["balancedLanePool"]!.DeepClone();
        File.WriteAllText(path, manifest.ToJsonString());

        var source = Path.Combine(root, "tests", "Mcg.AgentOrchestrator.Infrastructure.Tests");
        Directory.CreateDirectory(source);
        foreach (var name in new[] { "CliCommandTests", "ConductWatchSweepScopingTests", "GoalLifecycleEventWriterTests" })
            File.WriteAllText(Path.Combine(source, name + ".cs"),
                $"public class {name} {{ [Xunit.Fact] public void Executes() {{ }} }}");

        return AcceptanceInfrastructureLaneResolver.ResolveExecution(AcceptanceGateEngineSettings.Load(root), root).Lanes;
    }
}
