using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using static Mcg.AgentOrchestrator.Infrastructure.AcceptancePolicyShardPlanner;

// Parallel-safe: each test owns its temporary tree; policy overrides are execution scoped.
public sealed class ProjectScopedGatePolicyShardPlanTests : IDisposable
{
    private const string TestProject = "tests/Foreign.Tests/Foreign.Tests.csproj";
    private const string OtherTestProject = "tests/Other.Tests/Other.Tests.csproj";
    private const string MappedPath = "tests/Foreign.Tests/Changed.cs";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "project-shard-plan-" + Guid.NewGuid().ToString("N"));
    private string Worktree => Path.Combine(_root, "worktree");
    private string ProjectHome => Path.Combine(_root, "project-home");

    public ProjectScopedGatePolicyShardPlanTests()
    {
        Write(Worktree, ".git", "gitdir: isolated-fixture");
        Write(Worktree, "Foreign.sln", "");
        Write(Worktree, TestProject, "<Project />");
        Write(Worktree, OtherTestProject, "<Project />");
        Write(Worktree, MappedPath, "public class Changed { }");
        Write(Worktree, "src/Foreign/Changed.cs", "public class Changed { }");
        Write(ProjectHome, "acceptance-manifest.json", """
            {
              "engine": { "enforceStructuralCoverage": false },
              "checks": [
                { "name": "foreign tests", "type": "dotnet-test", "project": "tests/Foreign.Tests/Foreign.Tests.csproj" },
                { "name": "other tests", "type": "dotnet-test", "project": "tests/Other.Tests/Other.Tests.csproj" }
              ]
            }
            """);
    }

    [Fact]
    public void EffectivePlanUsesOnlyManifestProjectsAndHasNoCacheEligibility()
    {
        using var switches = AcceptanceShardPolicySwitches.Use(new(FullShards: false, ChangeScoped: true));
        var probe = CandidateTreeProbe.ForRepositoryRoot(Worktree);
        Assert.NotSame(CandidateTreeProbe.AssumeAllPresent, probe);
        Assert.All(ProjectTopology.Keys, project => Assert.False(probe.Exists(project)));
        var effective = CreatePlan([MappedPath]);
        var plan = effective.PolicyShardPlan;

        Assert.False(plan.Topology.IsHome);
        Assert.False(plan.ForceFull);
        Assert.Equal(TestProject, Assert.Single(plan.DependencyClosure));
        Assert.Equal($"changed projects: {TestProject}; dependency closure: {TestProject}", plan.Evidence);
        Assert.All(ProjectTopology.Keys, project => Assert.DoesNotContain(project, plan.Evidence));
        Assert.DoesNotContain("skipped absent test project", plan.Evidence);
        Assert.Empty(plan.AbsentProjects);
        Assert.Empty(CacheEligibleProjects(effective.Manifest.Checks.Select(check => check.Project), plan));
        Assert.Equal(new[] { "foreign tests", "other tests" }, effective.Checks.Select(check => check.Name));

        var shaResolutions = 0;
        var phase = AcceptanceDotnetBuildPhase.Create(Worktree, effective.Checks, [MappedPath], plan, _ =>
        {
            shaResolutions++;
            return "abc1234";
        });
        Assert.Null(phase.CachePlan);
        Assert.Equal(0, shaResolutions);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnyUnmappedBehaviorFileForcesAllManifestChecks(bool includeMappedPath)
    {
        using var switches = AcceptanceShardPolicySwitches.Use(new(FullShards: false, ChangeScoped: true));
        string[] changes = includeMappedPath ? [MappedPath, "src/Foreign/Changed.cs"] : ["src/Foreign/Changed.cs"];
        var effective = CreatePlan(changes);
        var plan = effective.PolicyShardPlan;

        Assert.True(plan.Applies);
        Assert.True(plan.ForceFull);
        Assert.Contains("manifest-declared project", plan.Evidence);
        Assert.Contains("full manifest checks", plan.Evidence);
        Assert.Equal(includeMappedPath ? new[] { TestProject } : [], plan.DependencyClosure.ToArray());
        Assert.Equal(effective.Manifest.Checks.Select(check => check.Name), effective.Checks.Select(check => check.Name));
        Assert.All(ProjectTopology.Keys, project => Assert.DoesNotContain(project, plan.Evidence));
        Assert.Empty(CacheEligibleProjects(effective.Manifest.Checks.Select(check => check.Project), plan));
    }

    [Fact]
    public void AbsenceProbesContainOnlyManifestDeclaredTestProjects()
    {
        using var switches = AcceptanceShardPolicySwitches.Use(new(FullShards: false, ChangeScoped: true));
        File.Delete(Path.Combine(Worktree, OtherTestProject));
        var effective = CreatePlan([MappedPath]);
        var plan = effective.PolicyShardPlan;

        Assert.Equal(OtherTestProject, Assert.Single(plan.AbsentProjects));
        Assert.Contains($"skipped absent test project: {OtherTestProject}", plan.Evidence);
        Assert.Equal("foreign tests", Assert.Single(effective.Checks).Name);
        Assert.All(ProjectTopology.Keys, project => Assert.DoesNotContain(project, plan.Evidence));
    }

    private GoalAcceptanceVerifier.EffectiveGatePlan CreatePlan(IReadOnlyList<string> changedFiles) =>
        GoalAcceptanceVerifier.CreateEffectiveGatePlan(Worktree, changedFiles,
            new AcceptanceGateEngineSettings { EnforceStructuralCoverage = false }, ProjectHome);

    private static void Write(string root, string relativePath, string text)
    {
        var path = Path.Combine(root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
