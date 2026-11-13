using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using static Mcg.AgentOrchestrator.Infrastructure.AcceptancePolicyShardPlanner;

// Parallel-safe: immutable topology inputs and a per-call injected candidate probe.
public sealed class NonHomeShardGraphGuardTests
{
    [Fact]
    public void ForeignHome_ExposesNoGraphMappingOrLinkedConsumers()
    {
        var topology = AcceptanceProjectTopologySource.Create("foreign-project-home", [NonHomeSelectionFixture.Project]);
        var linked = AcceptancePolicyShardPlannerLinkedSources.Table.First();
        Assert.NotEmpty(linked.Value);
        Assert.NotEmpty(AcceptanceProjectTopologySource.Home.LinkedConsumers(linked.Key));
        Assert.False(topology.IsHome);
        Assert.Empty(topology.Graph);
        Assert.Empty(topology.LinkedConsumers(linked.Key));
        var homeSource = CoreProject[..CoreProject.LastIndexOf('/')] + "/Changed.cs";
        Assert.Equal(CoreProject, AcceptanceProjectTopologySource.Home.MapPath(homeSource));
        Assert.Null(topology.MapPath(homeSource));
    }

    [Theory]
    [InlineData(NonHomeSelectionFixture.Source)]
    [InlineData(NonHomeSelectionFixture.Test)]
    [InlineData(NonHomeSelectionFixture.Source, NonHomeSelectionFixture.Test)]
    [InlineData("tools/build.ps1")]
    [InlineData("settings/widget.json")]
    [InlineData("Directory.Build.props")]
    public void ForeignChanges_KeepClosureAbsenceAndEvidenceWithinManifest(string first, string? second = null)
    {
        var topology = AcceptanceProjectTopologySource.Create("foreign-project-home", [NonHomeSelectionFixture.Project]);
        var homeProjects = ProjectTopology.Keys.Concat(ProjectTopology.Values.SelectMany(entry => entry.ReferencingProjects))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var plan = BuildPolicyShardPlan(second is null ? [first] : [first, second],
            new(false, true), new ForeignTreeProbe(homeProjects), topology);

        Assert.False(plan.Topology.IsHome);
        Assert.All(plan.DependencyClosure, project => Assert.Contains(project, topology.DeclaredProjects));
        Assert.All(plan.AbsentProjects, project => Assert.Contains(project, topology.DeclaredProjects));
        foreach (var project in homeProjects)
        {
            Assert.DoesNotContain(project, plan.DependencyClosure);
            Assert.DoesNotContain(project, plan.AbsentProjects);
            Assert.DoesNotContain(project, plan.Evidence, StringComparison.OrdinalIgnoreCase);
        }
        Assert.Empty(CacheEligibleProjects(homeProjects.Concat(topology.DeclaredProjects), plan));
    }

    private sealed class ForeignTreeProbe(HashSet<string> homeProjects) : ICandidateTreeProbe
    {
        public bool Exists(string path) => !homeProjects.Contains(path.Replace('\\', '/'));
    }
}
