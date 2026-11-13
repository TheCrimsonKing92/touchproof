using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using static Mcg.AgentOrchestrator.Infrastructure.AcceptancePolicyShardPlanner;

// Parallel-safe: immutable inputs and injected candidate probes; no ambient state.
public sealed class AcceptanceProjectTopologySourceTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void HomeSourcePreservesExistingPlanAndEligibility(string? projectHome)
    {
        var source = AcceptanceProjectTopologySource.Create(projectHome, null);
        Assert.Same(ProjectTopology, source.Graph);
        var changed = new[] { CoreProject[..CoreProject.LastIndexOf('/')] + "/Changed.cs" };
        var switches = new AcceptanceShardPolicySwitches(FullShards: false, ChangeScoped: true);
        var legacy = BuildPolicyShardPlan(changed, switches);
        var explicitHome = BuildPolicyShardPlan(changed, switches, CandidateTreeProbe.AssumeAllPresent, source);

        Assert.True(source.IsHome);
        Assert.Equal(legacy.Evidence, explicitHome.Evidence);
        Assert.Equal(legacy.Applies, explicitHome.Applies);
        Assert.Equal(legacy.ForceFull, explicitHome.ForceFull);
        Assert.True(legacy.DependencyClosure.SetEquals(explicitHome.DependencyClosure));
        Assert.Equal(CacheEligibleProjects(ProjectTopology.Keys, legacy),
            CacheEligibleProjects(ProjectTopology.Keys, explicitHome));
    }

    [Fact]
    public void UnavailableManifestDerivesNoProjectsOrCacheEligibility()
    {
        var source = AcceptanceProjectTopologySource.Create("project-home", null);
        var plan = BuildPolicyShardPlan(["src/Foreign/Changed.cs"],
            new(FullShards: false, ChangeScoped: true), new RejectingProbe(), source);

        Assert.False(source.IsHome);
        Assert.False(source.ManifestAvailable);
        Assert.Empty(source.Graph);
        Assert.Empty(source.DeclaredProjects);
        Assert.False(plan.Applies);
        Assert.False(plan.ForceFull);
        Assert.Empty(plan.DependencyClosure);
        Assert.Empty(plan.AbsentProjects);
        Assert.Empty(CacheEligibleProjects(ProjectTopology.Keys, plan));
        Assert.Contains("missing or unreadable", plan.Evidence);
        Assert.Contains("no project checks derived", plan.Evidence);
    }

    [Fact]
    public void DeclaredProjectsUseLongestUnambiguousDirectoryWithoutConsumerExpansion()
    {
        var source = AcceptanceProjectTopologySource.Create("project-home",
            ["src/Foreign/Foreign.csproj", "src\\Foreign\\nested\\Nested.csproj", "SRC/FOREIGN/FOREIGN.CSPROJ"]);
        var plan = BuildPolicyShardPlan(["src/foreign/nested/Changed.cs"],
            new(FullShards: false, ChangeScoped: true), CandidateTreeProbe.AssumeAllPresent, source);

        Assert.True(source.ManifestAvailable);
        Assert.Empty(source.Graph);
        Assert.Equal(2, source.DeclaredProjects.Count);
        Assert.False(plan.ForceFull);
        Assert.Equal("src/Foreign/nested/Nested.csproj", Assert.Single(plan.DependencyClosure));
        Assert.Equal("changed projects: src/Foreign/nested/Nested.csproj; dependency closure: src/Foreign/nested/Nested.csproj", plan.Evidence);
        Assert.Empty(CacheEligibleProjects(source.DeclaredProjects, plan));
    }

    [Theory]
    [InlineData("src/Foreign/One.csproj", "src/Foreign/Two.csproj")]
    [InlineData("src/Foreign/Foreign.sln", "Foreign.csproj")]
    [InlineData("C:/src/Foreign/Foreign.csproj", "/src/Foreign/Foreign.csproj")]
    public void UnmappableDeclarationsForceFullManifestFallback(string first, string second)
    {
        var source = AcceptanceProjectTopologySource.Create("project-home", [first, second]);
        var plan = BuildPolicyShardPlan(["src/Foreign/Changed.cs"],
            new(FullShards: false, ChangeScoped: true), CandidateTreeProbe.AssumeAllPresent, source);

        Assert.True(plan.ForceFull);
        Assert.Empty(plan.DependencyClosure);
        Assert.Contains("manifest-declared project", plan.Evidence);
        Assert.Contains("full manifest checks", plan.Evidence);
    }

    [Fact]
    public void EmptyAvailableManifestIsDistinctFromUnavailableManifest()
    {
        var source = AcceptanceProjectTopologySource.Create("project-home", []);
        var plan = BuildPolicyShardPlan(["src/Foreign/Changed.cs"],
            new(FullShards: false, ChangeScoped: true), CandidateTreeProbe.AssumeAllPresent, source);

        Assert.True(source.ManifestAvailable);
        Assert.True(plan.ForceFull);
        Assert.Empty(plan.DependencyClosure);
        Assert.Contains("full manifest checks", plan.Evidence);
    }

    private sealed class RejectingProbe : ICandidateTreeProbe
    {
        public bool Exists(string path) => throw new InvalidOperationException("Unavailable manifest must not derive absence probes.");
    }
}
