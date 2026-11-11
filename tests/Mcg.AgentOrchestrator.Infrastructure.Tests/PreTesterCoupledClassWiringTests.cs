using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using static ConductorDriverTests;
using Fixture = PreTesterCoupledClassSelectionsTests.Fixture;

// Parallel-safe: private files/kernel/attempts; launches and topology are seamed.
public sealed class PreTesterCoupledClassWiringTests
{
    private const string CandidateSha = "bbb2222";
    private const string Declared = "Core.Tests:DeclaredCoreTests";
    private const string Consumer = "Infrastructure.Tests:ConsumerTests";

    [Fact]
    public void PlannerCoupledSelectionReachesPreTesterRequest()
    {
        using var scenario = new Scenario();

        scenario.Driver().AdvanceOnce(scenario.Goal, ConductorAutonomyPolicy.Conservative);

        Assert.Equal(Declared + "; " + Consumer, Assert.Single(scenario.Requests));
        Assert.Equal([Declared, Consumer], scenario.Latest().Selections);
        Assert.Equal("green", scenario.Latest().Outcome);
    }

    [Fact]
    public void StructuralSelectionReachesPreTesterRequest()
    {
        using var scenario = new Scenario();
        scenario.Repository.Test("TreeReaderTests", prefix: "[LiveTreeStructural]");

        scenario.Driver().AdvanceOnce(scenario.Goal, ConductorAutonomyPolicy.Conservative);

        Assert.Contains("Infrastructure.Tests:TreeReaderTests", Assert.Single(scenario.Requests), StringComparison.Ordinal);
        Assert.Contains("Infrastructure.Tests:TreeReaderTests", scenario.Latest().Selections);
        Assert.Equal("green", scenario.Latest().Outcome);
    }

    [Theory]
    [InlineData("tests/Mcg.AgentOrchestrator.Infrastructure.Tests/SomeTests.cs")]
    [InlineData("config/production-member-reflection-exemptions.json")]
    [InlineData("src/Mcg.AgentOrchestrator.Core/SomeProductionType.cs")]
    public void ReflectionInputsReachRequestWithoutPaidStart(string changedPath)
    {
        using var scenario = new Scenario();
        scenario.Paths = [changedPath];
        scenario.Repository.Write(changedPath, changedPath.EndsWith(".cs", StringComparison.Ordinal)
            ? "public class ChangedInput {}" : "[]");
        // Omit the structural marker here so ordinary production content must select via
        // snapshot dependencies, even when topology information is unavailable to the fixture.
        scenario.Repository.Write("tests/Mcg.AgentOrchestrator.Infrastructure.Tests/ProductionMemberReflectionGuardTests.cs", """
            public class ProductionMemberReflectionGuardTests {
                [Fact] public void Scans() {
                    var snapshot = RepositorySourceSnapshotCache.Get(root);
                    snapshot.EnumerateFiles(Path.Combine(root, "src"));
                    snapshot.EnumerateFiles(Path.Combine(root, "tests"));
                    File.ReadAllText(Path.Combine(root, "config", "production-member-reflection-exemptions.json"));
                }
            }
            """);

        // Drive the production preflight seam through receipt composition, stopping before
        // AdvanceOnce's normal paid-dispatch continuation after a green preflight.
        Assert.False(scenario.Driver().TryRunDeveloperCompletionStructuralPreflight(
            scenario.Goal, scenario.Goal.Id.Value, ConductorAutonomyPolicy.Conservative,
            GoalLifecycleState.WorkspaceReady, out _));

        Assert.Contains("Infrastructure.Tests:ProductionMemberReflectionGuardTests", Assert.Single(scenario.Requests), StringComparison.Ordinal);
        Assert.Contains("Infrastructure.Tests:ProductionMemberReflectionGuardTests", scenario.Latest().Selections);
        Assert.Equal("green", scenario.Latest().Outcome);
        Assert.Equal(0, scenario.DispatchStarts);
    }

    [Theory(Timeout = TestHangGuard.WholeTestMilliseconds)]
    [Trait("Category", "CrossTick")]
    [InlineData(false)]
    [InlineData(true)]
    public void PreFeatureStartedAttemptKeepsIdentityAndClassList(bool originallyCoupled)
    {
        using var scenario = new Scenario();
        var launches = 0;
        if (!originallyCoupled) scenario.Paths = ["docs/notes.md"];
        else scenario.Repository.Test("PriorStructuralTests", prefix: "[LiveTreeStructural]");
        var coordinator = scenario.Background(() => launches++);
        scenario.Driver(coordinator).AdvanceOnce(scenario.Goal, ConductorAutonomyPolicy.Conservative);
        var original = scenario.Latest();
        Assert.Equal("started", original.Outcome);
        Assert.Equal(originallyCoupled ? new[] { Declared, Consumer, "Infrastructure.Tests:PriorStructuralTests" }.Order() :
            new[] { Declared }.Order(), original.Selections.Order());
        var attempt = Assert.Single(coordinator.GetUnreconciledAttempts([scenario.Goal.Id.Value]));
        scenario.Paths = [Fixture.Source];
        scenario.Repository.Test("NewlyCoupledTests", "NewContract");
        scenario.Repository.Test("NewStructuralTests", prefix: "[LiveTreeStructural]");

        var result = scenario.Driver(scenario.Background(() => launches++))
            .AdvanceOnce(scenario.Goal, ConductorAutonomyPolicy.Conservative);

        Assert.IsType<ConductorAdvanceOutcome.Held>(result.Outcome);
        Assert.Equal(original.Selections, scenario.Latest().Selections);
        Assert.Equal(original.NotRun, scenario.Latest().NotRun);
        Assert.Equal(original.ReceiptId, scenario.Latest().ReceiptId);
        var resumed = Assert.Single(coordinator.GetUnreconciledAttempts([scenario.Goal.Id.Value]));
        Assert.Equal(attempt.AttemptId, resumed.AttemptId);
        Assert.Equal(attempt.FocusedEvidenceReceiptId, resumed.FocusedEvidenceReceiptId);
        Assert.Equal(1, launches);
        Assert.Empty(scenario.Requests);
    }

    [Fact]
    public void NewClassesStayInBalancedRemainderLanes()
    {
        var settings = AcceptanceGateEngineSettings.Load(VerifiedRepositoryRoot.Find());
        var classes = new[] { typeof(PreTesterCoupledClassWiringTests), typeof(PreTesterCoupledClassSelectionsTests),
            typeof(ProductionMemberReflectionGuardTests), typeof(ProductionMemberReflectionScannerTests) }
            .Select(type => new AcceptanceTestClassDescriptor(type.FullName!, null)).ToArray();
        var expanded = AcceptanceBalancedLaneExpander.Expand(settings.InfrastructureTestLanes, classes,
            new Dictionary<string, double>(), settings.BalancedLanePool!);
        foreach (var item in classes)
        {
            var lane = Assert.Single(AcceptanceLaneMembership.LanesIncluding(expanded.Lanes, item.FullName));
            Assert.True(lane.Name.StartsWith(settings.BalancedLanePool!.LaneNamePrefix + " ", StringComparison.Ordinal) ||
                lane.Name == settings.BalancedLanePool.ResidualLaneName);
        }
    }

    private sealed class Scenario : IDisposable
    {
        internal string Root { get; } = ConductorDriverTests.CreateTempDirectory();
        internal AgentOrchestratorKernel Kernel { get; }
        internal Goal Goal { get; }
        private TaskSpec Tester { get; }
        internal Fixture Repository { get; }
        internal IReadOnlyList<string> Paths { get; set; } = [Fixture.Source];
        internal List<string> Requests { get; } = [];
        internal int DispatchStarts { get; private set; }
        internal Scenario()
        {
            (Kernel, Goal) = SoftwareGoal("Pre-Tester coupled evidence");
            var developer = Goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
            Tester = Goal.Tasks.Single(task => task.RequiredRole == AgentRole.Tester);
            foreach (var task in Goal.Tasks.TakeWhile(task => task.Id != developer.Id)) PassVerification(Kernel, Goal, task);
            DispatchTask(Kernel, Goal, developer, baseCommit: "aaa1111");
            var output = string.Join(Environment.NewLine, "WORKER_RESULT:", "files: " + Fixture.Source, "commands: none",
                "tests: deferred - DeclaredCoreTests", "commit: none", "blockers: none", "assigned_scope_complete: true",
                "model_fit: test/model - adequate - fixture", "skills: none", "confidence: high", "END_WORKER_RESULT");
            Kernel.RecordTaskVerification(Goal.Id, developer.Id, new("fixture", Root, 0, output, "", DateTimeOffset.UtcNow,
                WorkerResultPresent: true, HasCommittedChanges: true));
            Repository = new Fixture(GoalWorktrees.WorktreePath(Root, Goal.Id));
            Repository.Write(Fixture.Source, "public class NewContract {}");
            Repository.Test("ConsumerTests", "NewContract");
            Repository.Write("tests/Mcg.AgentOrchestrator.Core.Tests/DeclaredCoreTests.cs", "public class DeclaredCoreTests {}");
        }
        internal PreTesterEvidenceEntry Latest() => Assert.IsType<PreTesterEvidenceEntry>(
            PreTesterEvidenceIndexLines.Latest(Goal, Tester.Id, CandidateSha));
        internal ConductorParallelAcceptanceAttemptCoordinator Background(Action launched) => new(
            Path.Combine(Root, "attempts"), TrunkBranchName.Default, isProcessAlive: pid => pid == 7103,
            launchOwnedProcess: _ => { launched(); return new(7103); }, acquireStableSlotLease: (_, _) => null);
        internal ConductorDriver Driver(ConductorParallelAcceptanceAttemptCoordinator? coordinator = null)
        {
            var driver = MakeDriver(getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
                getPreReviewEvidenceContext: _ => NoPreReviewContext(CandidateSha), getLandingFileScopes: _ => Paths,
                getFindingEvidenceEngineSettings: _ => Repository.Settings,
                focusedEvidenceAttemptCoordinator: coordinator ?? new(Path.Combine(Root, "attempts"), TrunkBranchName.Default,
                    runInline: true, acquireStableSlotLease: (_, _) => null),
                runFocusedEvidence: (_, request) =>
                {
                    Requests.Add(request);
                    return ConductorDriverTestsFindingEvidenceReuse.RetainedEvidenceWithExecutedClasses(Root, request, CandidateSha);
                },
                dispatchAndStart: _ => { DispatchStarts++; return DispatchStartOutcome.Started(); },
                recordFindingEvidenceRequest: (goalId, taskId, message) => Kernel.RecordFindingEvidenceRequest(goalId, taskId, message),
                recordFindingEvidenceRun: (goalId, taskId, message) => Kernel.RecordFindingEvidenceRun(goalId, taskId, message),
                executionDirectory: Root);
            driver.OverrideDeveloperCompletionStructuralPreflightForTests(
                _ => new DeveloperCompletionStructuralFindings(false, "clean"), TimeSpan.FromSeconds(5));
            return driver;
        }
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
