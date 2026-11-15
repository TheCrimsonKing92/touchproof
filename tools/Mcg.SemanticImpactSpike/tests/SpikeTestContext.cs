using System.Reflection;
using Mcg.SemanticImpactSpike;
using Microsoft.Build.Locator;

internal static class SpikeTestContext
{
    internal static readonly string Root = Path.GetFullPath(Environment.GetEnvironmentVariable("MCG_ORCHESTRATOR_REPOSITORY_ROOT")
        ?? typeof(SpikeTestContext).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == "SpikeRepositoryRoot").Value!);

    static SpikeTestContext() { if (!MSBuildLocator.IsRegistered) MSBuildLocator.RegisterDefaults(); }

    internal static Dictionary<string, string> Properties(bool fixture = false)
    {
        var result = new Dictionary<string, string> { ["Configuration"] = "Debug" };
        var key = fixture ? "SpikeBuildArtifactsPath" : "SpikeAnalysisArtifactsPath";
        var path = typeof(SpikeTestContext).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == key).Value;
        if (!string.IsNullOrEmpty(path)) { result["UseArtifactsOutput"] = "true"; result["ArtifactsPath"] = path; }
        return result;
    }
    internal static Task<CheckResult[]> Fixture() => SpikeChecks.FixtureAsync(Root, Properties(fixture: true));
}
