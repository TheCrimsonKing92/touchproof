using System.Text.Json;
using System.Xml.Linq;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using static Mcg.AgentOrchestrator.Infrastructure.AcceptancePolicyShardPlanner;
using AcceptanceManifestCheck = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.AcceptanceManifestCheck;

/// <summary>
/// Non-home selection audit. Ranges refer to the owning guard or its scoped consumer.
/// | Component | Source path and lines | Disposition and boundary |
/// | --- | --- | --- |
/// | Shard graph/constants | src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/AcceptancePolicyShardPlanner.cs:8-60 | home-only: graph exposed only by home topology |
/// | Topology | src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/AcceptanceProjectTopologySource.cs:8-45 | manifest-derived: empty foreign graph, declared path mapping |
/// | Gate integration | src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/GoalAcceptanceVerifier.cs:1789-1833,3822-3830 | manifest-derived: load before planning, throw on unavailable manifest |
/// | Build cache | src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/GoalAcceptanceVerifier.DotnetBuildPhase.cs:74-149 | home-only: cache projects come from topology graph |
/// | Planner routing | src/Mcg.AgentOrchestrator.Core/Application/RepositoryTestImpactPlanner.cs:74-114,194-205,687-713 | manifest-derived: foreign entry points scope built-in routing output |
/// | Candidate filtering | src/Mcg.AgentOrchestrator.Core/Application/RepositoryTestImpactPlanner.CandidateTree.cs:5-54 | fail-closed: absent built-ins removed; foreign notice, not a manifest loader |
/// | Optimistic probe | src/Mcg.AgentOrchestrator.Core/Application/CandidateTreeProbe.cs:13-30 | manifest-derived: optimistic output is filtered at scoped consumers |
/// | Foreign planner | src/Mcg.AgentOrchestrator.Core/Application/ForeignTargetTestImpactPlanner.cs:15-36 | manifest-derived: built-ins absent, worker consumer scopes to manifest |
/// | Reverse dependency | src/Mcg.AgentOrchestrator.Core/Application/ReverseDependencyTestImpactReader.cs:9-10,43-55 | home-only: planner consults reader only for Mcg Core source prefix |
/// | Inventory | src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/DeclaredTestProjectInventory.cs:16-20,27-114 | manifest-derived: shape and declaration govern resolution; legacy help is not selection |
/// | Goal scope | src/Mcg.AgentOrchestrator.App/Orchestration/GoalTargetImpactPlanScope.cs:9-64 | fail-closed: rootless non-home build plan uses existing missing-manifest scope |
/// | Rootless compiler | src/Mcg.AgentOrchestrator.Core/Application/VerificationPolicyCompiler.cs:37-55 | home-only: no production callers; all production policies receive scoped plans |
/// | Reuse shadow | src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/AcceptanceTestReuseShadow.cs:51-81 | home-only: cache factory requires Mcg Infrastructure partition (verifier:2082-2110) |
/// | Train attribution | src/Mcg.AgentOrchestrator.App/Orchestration/MergeTrainImpactAttribution.cs:9-83 | fail-closed: non-home runs return null without Mcg selections/project file; emits no checks |
/// | Gate scope | src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/ManifestScopedImpactPlan.cs:29-50 | manifest-derived: filter executable plans to declared checks |
/// </summary>
// Parallel-safe: each case owns its repository, linked worktree, manifest and TRX.
// Home control only reads the verified repository; shard switches are scoped with AsyncLocal.
public sealed class NonHomeManifestDerivedSelectionAuditTests
{
    private const string ForeignProject = "tests/Widget/Specs/Widget.Tests.csproj";
    private const string ForeignTest = "tests/Widget/Specs/WidgetTests.cs";
    private const string ForeignSource = "src/Widget/Product/Widget.cs";
    private const string ManifestName = "acceptance-manifest.json";
    private const string Mcg = "Mcg.AgentOrchestrator";
    private static readonly string[] Changes = [ForeignSource, ForeignTest];
    private const string Manifest = """
        {"engine":{"enforceStructuralCoverage":false},"checks":[
          {"name":"widget tests","type":"dotnet-test","project":"tests/Widget/Specs/Widget.Tests.csproj"}]}
        """;

    [Theory]
    [InlineData("optimistic")]
    [InlineData("solution")]
    [InlineData("plain")]
    public void DeclaredManifest_BoundsEveryComponent(string layout)
    {
        using var fixture = new Fixture(layout, "declared");
        using var switches = AcceptanceShardPolicySwitches.Use(new(FullShards: false, ChangeScoped: true));
        AssertProbeControl(fixture, layout);
        var settings = AcceptanceGateEngineSettings.Load(fixture.Worktree, fixture.Home);
        var gate = GoalAcceptanceVerifier.CreateEffectiveGatePlan(fixture.Worktree, Changes, settings, fixture.Home);
        Assert.Equal(ForeignProject, Assert.Single(gate.Checks).Project);
        Assert.Empty(gate.PolicyRequiredChecks);
        Assert.Empty(gate.PolicyShardPlan.Topology.Graph);
        Assert.Equal(new[] { ForeignProject }, gate.PolicyShardPlan.Topology.DeclaredProjects);
        AssertCacheEmpty(fixture, gate.Checks, gate.PolicyShardPlan);

        // A changed declared test path maps to that project without expanding Mcg consumers.
        var shard = BuildPolicyShardPlan([ForeignTest], new(false, true),
            CandidateTreeProbe.ForRepositoryRoot(fixture.Worktree), gate.PolicyShardPlan.Topology);
        Assert.Equal(ForeignProject, Assert.Single(shard.DependencyClosure));
        Assert.Empty(shard.Topology.LinkedConsumers(ForeignSource));
        AssertNoMcg(new { shard.Evidence, shard.DependencyClosure, shard.AbsentProjects, gate.Checks });

        var plans = ScopedPlans(fixture, gate.Manifest.Checks);
        Assert.Equal(ForeignProject, Assert.Single(plans[0].Checks).Command[3]);
        foreach (var plan in plans)
        {
            AssertNoMcg(plan);
            Assert.Null(plan.ReverseDependencyDegradation);
            Assert.Null(plan.ReverseDependencyHeadroom);
            Assert.All(plan.Checks.Where(check => check.Command.Count > 0),
                check => Assert.Equal(ForeignProject, check.Command[3]));
            AssertNoMcg(Compile(plan));
        }

        Assert.Equal(new[] { ForeignProject }, DeclaredTestProjectInventory.DeclaredProjects(settings, gate.Manifest.Checks));
        Assert.True(DeclaredTestProjectInventory.TryResolve("Widget.Tests", settings, out var resolved, gate.Manifest.Checks));
        Assert.Equal(ForeignProject, resolved);
        foreach (var alias in new[] { "Core", CoreTestsProject })
            Assert.False(DeclaredTestProjectInventory.TryResolve(alias, settings, out _, gate.Manifest.Checks));

        // The partition predicate is the cache factory's prerequisite for constructing a reuse shadow.
        var homePartition = new AcceptanceManifestCheck { Name = "infrastructure tests: audit", Type = "dotnet-test",
            Project = InfrastructureTestsProject, Arguments = ["--filter", "FullyQualifiedName~WidgetTests"] };
        Assert.True(GoalAcceptanceVerifier.TryGetInfrastructurePartitionId(homePartition, out _, out _));
        Assert.All(gate.Checks, check =>
            Assert.False(GoalAcceptanceVerifier.TryGetInfrastructurePartitionId(check, out _, out _)));

        // The unused changed-files compiler overload is intentionally Mcg-specific; foreign callers pass a plan.
        Assert.Contains(Mcg, JsonSerializer.Serialize(VerificationPolicyCompiler.Compile(
            AgentRole.Developer, "", "", null, Changes)));
    }

    [Theory]
    [InlineData("optimistic", "missing")]
    [InlineData("optimistic", "unreadable")]
    [InlineData("solution", "missing")]
    [InlineData("solution", "unreadable")]
    [InlineData("plain", "missing")]
    [InlineData("plain", "unreadable")]
    public void UnavailableManifest_DerivesNoChecks(string layout, string state)
    {
        using var fixture = new Fixture(layout, state);
        AssertProbeControl(fixture, layout);
        Assert.Null(WorkerManifestTestCheckReader.Read(fixture.ManifestPath));
        var scoped = GoalTargetImpactPlanScope.Plan(RepositoryChangeClassifier.Classify(Changes), fixture.Worktree, fixture.Target);
        var worker = WorkerTestImpactPlanSelection.For(Changes, fixture.Worktree, fixture.Target);
        foreach (var plan in new[] { scoped, worker })
        {
            Assert.All(plan.Checks, check => Assert.Empty(check.Command));
            Assert.Contains($"acceptance manifest is {state} at", plan.Summary);
            Assert.Contains(ManifestName, plan.Summary);
            AssertNoMcg(plan);
            AssertNoMcg(Compile(plan));
        }

        var topology = AcceptanceProjectTopologySource.Create(fixture.Home, null);
        var shard = BuildPolicyShardPlan(Changes, new(false, true),
            CandidateTreeProbe.ForRepositoryRoot(fixture.Worktree), topology);
        Assert.Empty(shard.Topology.Graph);
        Assert.Empty(shard.Topology.DeclaredProjects);
        Assert.Empty(shard.DependencyClosure);
        Assert.Empty(shard.AbsentProjects);
        Assert.Contains("manifest missing or unreadable; no project checks derived", shard.Evidence);
        AssertCacheEmpty(fixture, [], shard);
        Assert.Empty(DeclaredTestProjectInventory.DeclaredProjects(null));
        Assert.Empty(ManifestScopedImpactPlan.Plan(Changes, fixture.Worktree, []).Checks.Where(check => check.Command.Count > 0));

        IReadOnlyList<AcceptanceManifestCheck>? checks = null;
        void ReadGate() => checks = GoalAcceptanceVerifier.BuildEffectiveAcceptanceChecksForTests(fixture.Worktree, Changes, fixture.Home);
        if (state == "missing")
        {
            var failure = Assert.Throws<MissingAcceptanceManifestException>(ReadGate);
            foreach (var location in AcceptanceManifestLocator.SearchedLocations(fixture.Worktree, fixture.Home))
                Assert.Contains(location, failure.Message);
        }
        else
            Assert.ThrowsAny<JsonException>(ReadGate);
        Assert.Null(checks);
    }

    [Fact]
    public void RootlessNonHome_FailsClosedWithoutResolvingRelativeManifest()
    {
        var summary = RepositoryChangeClassifier.Classify(Changes);
        var raw = RepositoryTestImpactPlanner.Plan(summary);
        Assert.Contains(Mcg, JsonSerializer.Serialize(raw.Checks));
        var target = new WorkerTargetHome(false, _ => throw new InvalidOperationException("A rootless plan cannot resolve a relative manifest."));
        foreach (var root in new string?[] { null, "", " " })
        {
            var actual = GoalTargetImpactPlanScope.Plan(summary, root, target);
            Assert.Empty(actual.Checks);
            Assert.Equal("acceptance manifest is missing at (unspecified); no project checks derived", actual.Summary);
            AssertNoMcg(actual);
        }
        var empty = RepositoryChangeClassifier.Classify([]);
        AssertPlansEqual(RepositoryTestImpactPlanner.Plan(empty), GoalTargetImpactPlanScope.Plan(empty, null, target));
    }

    [Theory]
    [InlineData("declared")]
    [InlineData("missing")]
    [InlineData("unreadable")]
    public void NonHomeTrain_CannotAttributeForeignFailure(string state)
    {
        using var fixture = new Fixture("optimistic", state);
        var trx = Path.Combine(fixture.Root, "red.trx");
        new XDocument(new XElement("TestRun", new XElement("Results",
            new XElement("UnitTestResult", new XAttribute("testName", "WidgetTests.Fails"),
                new XAttribute("outcome", "Failed"))))).Save(trx);
        var read = AcceptanceTrxFailureReader.Read(trx);
        Assert.Equal(AcceptanceTrxReadStatus.Readable, read.Status);
        Assert.Single(read.Failures);
        foreach (var paths in new[] { new[] { ForeignSource }, new[] { ForeignSource, "config/widget.json" } })
        {
            var members = new[] { Member('a', paths), Member('b', [ForeignTest]) };
            Assert.Null(MergeTrainImpactAttribution.TryAttribute([trx], fixture.Worktree, members, out var classes));
            Assert.Empty(classes);
        }
    }

    [Fact]
    public void HomeControl_PreservesMcgPlansTopologyAndCache()
    {
        var root = VerifiedRepositoryRoot.Find();
        string[] changes = ["tests/Mcg.AgentOrchestrator.Core.Tests/VerificationPolicyCompilerTests.cs"];
        var summary = RepositoryChangeClassifier.Classify(changes);
        var expected = RepositoryTestImpactPlanner.Plan(summary, root);
        Assert.Contains(expected.Checks, check => check.Command.Contains(
            "tests/Mcg.AgentOrchestrator.Core.Tests/Mcg.AgentOrchestrator.Core.Tests.csproj"));
        var home = new WorkerTargetHome(true, _ => throw new InvalidOperationException("Home needs no foreign manifest."));
        AssertPlansEqual(expected, GoalTargetImpactPlanScope.Plan(summary, root, home));
        AssertPlansEqual(expected, GoalTargetImpactPlanScope.Plan(summary, root, null));
        AssertPlansEqual(expected, WorkerTestImpactPlanSelection.For(changes, root, home));
        AssertPlansEqual(RepositoryTestImpactPlanner.Plan(summary), GoalTargetImpactPlanScope.Plan(summary, null, home));
        AssertPlansEqual(RepositoryTestImpactPlanner.Plan(summary), GoalTargetImpactPlanScope.Plan(summary, null, null));

        var topology = AcceptanceProjectTopologySource.Create(null, null);
        Assert.Same(ProjectTopology, topology.Graph);
        var shard = BuildPolicyShardPlan(changes, new(false, true), CandidateTreeProbe.ForRepositoryRoot(root), topology);
        Assert.Contains("tests/Mcg.AgentOrchestrator.Core.Tests/Mcg.AgentOrchestrator.Core.Tests.csproj", shard.DependencyClosure);
        Assert.Contains("tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj", shard.DependencyClosure);
        var legacy = BuildPolicyShardPlan(changes, new(false, true), CandidateTreeProbe.ForRepositoryRoot(root));
        Assert.True(legacy.DependencyClosure.SetEquals(shard.DependencyClosure));
        var cache = CacheEligibleProjects(topology.Graph.Keys, shard);
        Assert.Contains("src/Mcg.AgentOrchestrator.Core/Mcg.AgentOrchestrator.Core.csproj", cache);
        Assert.Contains("tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj", cache);
        Assert.Equal(CacheEligibleProjects(topology.Graph.Keys, legacy), cache);
    }

    private static RepositoryTestImpactPlan[] ScopedPlans(Fixture fixture, IReadOnlyList<AcceptanceManifestCheck> checks) =>
    [
        GoalTargetImpactPlanScope.Plan(RepositoryChangeClassifier.Classify(Changes), fixture.Worktree, fixture.Target),
        WorkerTestImpactPlanSelection.For(Changes, fixture.Worktree, fixture.Target),
        ManifestScopedImpactPlan.Plan(Changes, fixture.Worktree, checks)
    ];

    private static VerificationPolicy Compile(RepositoryTestImpactPlan plan) =>
        VerificationPolicyCompiler.Compile(AgentRole.Developer, "", "", null, Changes, plan);

    private static void AssertNoMcg<T>(T value) => Assert.DoesNotContain(Mcg, JsonSerializer.Serialize(value));

    private static void AssertPlansEqual(RepositoryTestImpactPlan expected, RepositoryTestImpactPlan actual) =>
        Assert.Equal(JsonSerializer.Serialize(expected), JsonSerializer.Serialize(actual));

    private static void AssertCacheEmpty(Fixture fixture, IReadOnlyList<AcceptanceManifestCheck> checks, PolicyShardPlan shard)
    {
        Assert.Empty(CacheEligibleProjects(checks.Select(check => check.Project!).OfType<string>(), shard));
        var phase = AcceptanceDotnetBuildPhase.Create(fixture.Worktree, checks, Changes, shard,
            _ => throw new InvalidOperationException("Non-home topology cannot resolve a base-cache SHA."));
        Assert.Null(phase.CachePlan);
        AssertNoMcg(phase.BuildArguments);
    }

    private static void AssertProbeControl(Fixture fixture, string layout)
    {
        var probe = CandidateTreeProbe.ForRepositoryRoot(fixture.Worktree);
        var raw = RepositoryTestImpactPlanner.Plan(Changes, fixture.Worktree);
        if (layout == "solution")
        {
            Assert.NotSame(CandidateTreeProbe.AssumeAllPresent, probe);
            Assert.All(raw.Checks, check => Assert.Empty(check.Command));
        }
        else
        {
            Assert.Same(CandidateTreeProbe.AssumeAllPresent, probe);
            Assert.Contains(Mcg, JsonSerializer.Serialize(raw.Checks));
        }
    }

    private static MergeTrainMemberBinding Member(char id, string[] paths) => new(new GoalId(new string(id, 32)),
        new string(id, 40), new string(id, 40), paths, [$"member:{id}"], ChangeRiskTier.Behavior,
        ConductorTransitionDecision.Auto, GateReadyMergeStatus.Clean.ToString(),
        GateReadyMergeReason.NoConflictsDetected.ToString(), new string(id, 40));

    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = SharedTestSupport.CreateTempDirectory();
        internal string Home => Path.Combine(Root, "project-home");
        internal string ManifestPath => AcceptanceManifestLocator.Resolve(Worktree, Home);
        internal string Worktree { get; }
        internal WorkerTargetHome Target => new(false, worktree => AcceptanceManifestLocator.Resolve(worktree, Home));

        internal Fixture(string layout, string state)
        {
            var repository = Path.Combine(Root, "repository");
            Directory.CreateDirectory(repository);
            MainBranchGitRepositoryTemplate.CopyTo(repository, includeSkillCatalog: false);
            Write(repository, "src/Widget/Product/Widget.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
            Write(repository, ForeignSource, "public class Widget { }");
            Write(repository, ForeignProject, "<Project Sdk=\"Microsoft.NET.Sdk\" />");
            Write(repository, ForeignTest, "public class WidgetTests { [Xunit.Fact] public void Fails() { } }");
            Write(repository, "config/widget.json", "{}");
            if (layout == "solution") Write(repository, "Widget.sln", "Microsoft Visual Studio Solution File, Format Version 12.00");
            Git(repository, "add", ".");
            Git(repository, "commit", "-m", "Foreign target layout");
            Worktree = Path.Combine(Root, "candidate");
            Git(repository, "worktree", "add", "-b", "audit-candidate", Worktree, "HEAD");
            Assert.True(File.Exists(Path.Combine(Worktree, ".git")));
            if (layout == "plain")
            {
                var plain = Path.Combine(Root, "plain");
                foreach (var path in new[] { ForeignSource, ForeignProject, ForeignTest, "src/Widget/Product/Widget.csproj" })
                    Write(plain, path, File.ReadAllText(Path.Combine(Worktree, path)));
                Worktree = plain;
            }
            Directory.CreateDirectory(Home);
            if (state != "missing") Write(Home, ManifestName, state == "unreadable" ? "{ broken json" : Manifest);
        }

        private static void Git(string root, params string[] args)
        {
            var result = GitCli.Run(root, args);
            Assert.True(result.Succeeded, $"git {string.Join(' ', args)}: {result.Error}");
        }

        private static void Write(string root, string path, string content)
        {
            var fullPath = Path.Combine(root, path);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllText(fullPath, content);
        }

        public void Dispose() => SharedTestSupport.RemoveTempDirectory(Root);
    }
}
