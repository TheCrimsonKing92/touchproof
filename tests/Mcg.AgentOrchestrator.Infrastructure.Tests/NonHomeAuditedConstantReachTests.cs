using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using static Mcg.AgentOrchestrator.Infrastructure.AcceptancePolicyShardPlanner;

// Parallel-safe: each case owns its foreign files and project home; switches use AsyncLocal.
public sealed class NonHomeAuditedConstantReachTests
{
    [Fact]
    public void ForeignReader_MissingDependentProject_PreservesFullExecution()
    {
        using var fixture = new NonHomeSelectionFixture();
        var settings = AcceptanceGateEngineSettings.Load(fixture.Worktree, fixture.Home);
        string[] changes = [NonHomeSelectionFixture.Source];
        var selection = ReverseDependencyTestImpactReader.Read(fixture.Worktree, changes, bypassCache: true);
        Assert.Equal(ReverseDependencySelectionOutcome.Unreadable, selection.Outcome);
        Assert.Empty(selection.TestClassNames);
        Assert.False(ReverseDependencyTestImpactReader.IsDeliberatelyPartialRepositoryRoot(fixture.Worktree));
        var live = AcceptanceImpactSelectionReader.ReadLive(fixture.Worktree, changes, settings);
        Assert.Equal(ReverseDependencySelectionOutcome.Unreadable, live.Selection.Outcome);
        var session = new AcceptanceSelectedExecutionSession(AcceptanceSelectedExecutionMode.Enforce,
            fixture.Worktree, changes, engineSettings: settings);
        AcceptanceTestLane[] lanes = [new("Widget", "FullyQualifiedName~WidgetTests")];
        Assert.Same(lanes, session.Transform(lanes));
        Assert.False(session.Applied);
        var record = session.Observe(new(AcceptanceGateLanePlan.CurrentSchemaVersion, "foreign-attempt", [], [])).SelectedExecution!;
        Assert.Equal("full-gate", record.Decision);
        Assert.Equal("Unreadable", record.Outcome);
        Assert.Equal(live.Selection.Reason, record.FullGateReason);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ForeignProbeAndCache_KeepGateInputsWithinDeclarations(bool solution)
    {
        using var fixture = new NonHomeSelectionFixture(solution);
        using var switches = AcceptanceShardPolicySwitches.Use(new(false, true));
        var probe = CandidateTreeProbe.ForRepositoryRoot(fixture.Worktree);
        if (solution) Assert.NotSame(CandidateTreeProbe.AssumeAllPresent, probe);
        else Assert.Same(CandidateTreeProbe.AssumeAllPresent, probe);
        var settings = AcceptanceGateEngineSettings.Load(fixture.Worktree, fixture.Home);
        string[] changes = [NonHomeSelectionFixture.Source, NonHomeSelectionFixture.Test];
        var gate = GoalAcceptanceVerifier.CreateEffectiveGatePlan(fixture.Worktree, changes, settings, fixture.Home);
        var declared = ManifestDeclaredTestProjects.From(gate.Manifest.Checks.Select(check => check.Project));
        Assert.False(gate.PolicyShardPlan.Topology.IsHome);
        Assert.Empty(gate.PolicyShardPlan.Topology.Graph);
        Assert.All(gate.Checks, check => Assert.True(declared.Declares(check.Project)));
        Assert.Equal(NonHomeSelectionFixture.Project, Assert.Single(gate.Checks).Project);
        Assert.All(gate.PolicyShardPlan.AbsentProjects, project => Assert.True(declared.Declares(project)));
        Assert.All(gate.PolicyShardPlan.DependencyClosure, project => Assert.True(declared.Declares(project)));
        Assert.Empty(CacheEligibleProjects(ProjectTopology.Keys.Concat([NonHomeSelectionFixture.Project]), gate.PolicyShardPlan));
        var phase = AcceptanceDotnetBuildPhase.Create(fixture.Worktree, gate.Checks, changes, gate.PolicyShardPlan,
            _ => throw new InvalidOperationException("Foreign topology must not resolve a base-build identity."));
        Assert.Null(phase.CachePlan);

        Assert.Equal(new[] { NonHomeSelectionFixture.Project }, DeclaredTestProjectInventory.DeclaredProjects(settings, gate.Manifest.Checks));
        Assert.True(DeclaredTestProjectInventory.TryResolve("Widget.Tests", settings, out var resolved, gate.Manifest.Checks));
        Assert.Equal(NonHomeSelectionFixture.Project, resolved);
        foreach (var alias in new[] { "Core", "Core.Tests", "Mcg.AgentOrchestrator.Core.Tests", "Infrastructure",
            "Infrastructure.Tests", "Mcg.AgentOrchestrator.Infrastructure.Tests", CoreTestsProject, InfrastructureTestsProject })
            Assert.False(DeclaredTestProjectInventory.TryResolve(alias, settings, out _, gate.Manifest.Checks));
    }
}
