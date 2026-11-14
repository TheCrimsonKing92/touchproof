using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using static ConductorDriverTests;

// Parallel-safe: private files/kernel/attempts; all process launches are seamed.
public sealed class PreTesterCliSurfaceWiringTests
{
    private const string CandidateSha = "bbb2222";
    private const string ChangedPath = "src/Mcg.AgentOrchestrator.App/Cli/AttentionHandler.cs";
    private const string CoreSelection = "Core.Tests:DeclaredCoreTests";
    private const string Golden = "CliAttentionNextGoldenOutputTests";
    private const string Writer = "CliAttentionNextReadOnlyWriterHeldTests";
    private static string Cli(string name) => "Infrastructure.Cli.Tests:" + name;

    [Fact]
    public void NewAttemptSelectsEntireCliInventoryAndExecutesOverlappingDeclarationOnce()
    {
        using var scenario = new Scenario("deferred - DeclaredCoreTests, " + Golden);
        var driver = scenario.Driver(scenario.Green);

        driver.AdvanceOnce(scenario.Goal, ConductorAutonomyPolicy.Conservative);

        var request = Assert.Single(scenario.Requests);
        Assert.Equal([CoreSelection, Cli(Golden), Cli(Writer)], Selections(request));
        Assert.Equal(1, Selections(request).Count(item => item == Cli(Golden)));
        Assert.Equal(Selections(request), scenario.Latest().Selections);
        Assert.Equal("green", scenario.Latest().Outcome);
        Assert.Equal([AgentRole.Tester], scenario.Roles);
        Assert.Equal(0, scenario.GateRuns);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CliWideRedUsesOrdinaryAttributionUnlessIndependentSourceGuardMatches(bool sourceGuard)
    {
        using var scenario = new Scenario();
        if (sourceGuard)
            scenario.DeclareCli(Golden, "[Fact] public void Output() { File.ReadAllText(\"" + ChangedPath + "\"); }");
        var failure = Golden + ".Output";
        var driver = scenario.Driver(request => CandidateOnlyRed(request, failure));

        driver.AdvanceOnce(scenario.Goal, ConductorAutonomyPolicy.Conservative);

        Assert.Contains(Cli(Golden), Selections(Assert.Single(scenario.Requests)));
        Assert.Equal(sourceGuard ? "actionable-red" : "red", scenario.Latest().Outcome);
        Assert.Equal([failure], scenario.Latest().FailingTests);
        Assert.Equal(sourceGuard ? new[] { AgentRole.Developer } : [AgentRole.Tester], scenario.Roles);
        Assert.Equal(sourceGuard ? 1 : 0, scenario.Feedback.Count);
        Assert.Equal(0, scenario.GateRuns);
    }

    [Fact]
    public void OrdinaryCliRedRemainsEligibleForExistingCrossGoalRerunRules()
    {
        using var scenario = new Scenario();
        var failure = Golden + ".Output";
        var driver = scenario.Driver(request => scenario.Requests.Count == 1
            ? CandidateOnlyRed(request, failure) : scenario.Green(request));
        driver.PreTesterFailureHistory = (_, _) => new Dictionary<string, IReadOnlyList<string>>
        {
            [CrossGoalTestFailureHistory.TestKey(failure)!] = ["cccccccc-other-goal"]
        };
        driver.AdvanceOnce(scenario.Goal, ConductorAutonomyPolicy.Conservative);
        Assert.Equal("red", scenario.Latest().Outcome);
        Assert.Equal(2, scenario.Requests.Count);
        Assert.Equal(Cli(Golden), scenario.Requests[1]);
        Assert.Empty(scenario.Feedback);
        var marker = Assert.Single(scenario.Goal.Timeline.Select(evt =>
            PreTesterOutOfDiffRerunMarker.Parse(evt.Message)).OfType<PreTesterOutOfDiffRerunMarker>());
        Assert.Equal("non-blocking", marker.Verdict);
        Assert.Equal("green", marker.Rerun);
        Assert.Equal([AgentRole.Tester], scenario.Roles);
    }

    [Fact(Timeout = TestHangGuard.WholeTestMilliseconds)]
    [Trait("Category", "CrossTick")]
    public void StartedCliAndStructuralAttemptReconcilesInCreationOrder()
    {
        using var scenario = new Scenario();
        const string structural = "Infrastructure.Tests:WiringTreeReaderTests";
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(scenario.CliDirectory)!, "WiringTreeReaderTests.cs"),
            "[LiveTreeStructural] public class WiringTreeReaderTests { [Fact] public void Reads() {} }");
        var launches = 0;
        var driver = scenario.Driver(_ => throw new Xunit.Sdk.XunitException("Unexpected inline run"),
            scenario.Background(() => launches++));

        driver.AdvanceOnce(scenario.Goal, ConductorAutonomyPolicy.Conservative);

        var original = scenario.Latest();
        Assert.Equal("started", original.Outcome);
        Assert.Equal([CoreSelection, Cli(Golden), Cli(Writer), structural], original.Selections);
        Assert.Equal(1, launches);
        Assert.Empty(scenario.Requests);
        var guards = PreTesterGuardSelections.Create(new(), scenario.Worktree, scenario.Paths,
            "fixture-main-revision", settings: scenario.Settings, priorSelections: original.Selections);
        var augmented = guards.Augment(new([new("Core.Tests", "DeclaredCoreTests")], []),
            scenario.Settings, original, ConductorDriver.FormatFindingEvidenceSelection);
        Assert.Equal(original.Selections, augmented.Selections.Select(ConductorDriver.FormatFindingEvidenceSelection));
        var inline = new ConductorParallelAcceptanceAttemptCoordinator(
            Path.Combine(scenario.Root, "inline-attempts"), TrunkBranchName.Default,
            runInline: true, acquireStableSlotLease: (_, _) => null);
        var resumedDriver = scenario.Driver(scenario.Green, inline);

        var exception = Record.Exception(() => resumedDriver.AdvanceOnce(scenario.Goal, ConductorAutonomyPolicy.Conservative));

        Assert.Null(exception);
        Assert.Equal(original.Selections, Selections(Assert.Single(scenario.Requests)));
        Assert.Equal("green", scenario.Latest().Outcome);
        Assert.Equal(original.Selections, scenario.Latest().Selections);
        Assert.Equal(original.NotRun, scenario.Latest().NotRun);
        Assert.Equal(original.ReceiptId, scenario.Latest().ReceiptId);
        Assert.Equal([AgentRole.Tester], scenario.Roles);
        Assert.Equal(0, scenario.GateRuns);
    }

    [Theory(Timeout = TestHangGuard.WholeTestMilliseconds)]
    [Trait("Category", "CrossTick")]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void ResumedAttemptKeepsRecordedMembershipIdentityAndSingleLaunch(bool originallyCli, bool unresolvedDeclaration)
    {
        using var scenario = new Scenario("deferred - DeclaredCoreTests" +
            (unresolvedDeclaration ? ", MissingDeclaredTests" : ""));
        if (!originallyCli) scenario.Paths = ["src/Other.cs"];
        var launches = 0;
        var coordinator = scenario.Background(() => launches++);
        var driver = scenario.Driver(_ => throw new Xunit.Sdk.XunitException("Unexpected inline run"), coordinator);
        driver.AdvanceOnce(scenario.Goal, ConductorAutonomyPolicy.Conservative);
        var original = scenario.Latest();
        Assert.Equal("started", original.Outcome);
        Assert.Equal(originallyCli ? new[] { CoreSelection, Cli(Golden), Cli(Writer) } : [CoreSelection], original.Selections);
        Assert.Equal(unresolvedDeclaration ? new[] { "MissingDeclaredTests" } : [], original.NotRun);
        var attempt = Assert.Single(coordinator.GetUnreconciledAttempts([scenario.Goal.Id.Value]));
        scenario.Paths = [ChangedPath];
        scenario.DeclareCli("NewlyDiscoveredTests");
        File.Delete(Path.Combine(scenario.CliDirectory, Golden + ".cs"));
        File.Delete(scenario.CliProjectPath); // Resuming must not resolve a fresh CLI inventory.

        var held = scenario.Driver(_ => throw new Xunit.Sdk.XunitException("Unexpected inline run"),
            scenario.Background(() => launches++)).AdvanceOnce(scenario.Goal, ConductorAutonomyPolicy.Conservative);

        Assert.IsType<ConductorAdvanceOutcome.Held>(held.Outcome);
        Assert.Equal(original.Selections, scenario.Latest().Selections);
        Assert.Equal(original.NotRun, scenario.Latest().NotRun);
        Assert.Equal(original.ReceiptId, scenario.Latest().ReceiptId);
        Assert.Equal("started", scenario.Latest().Outcome);
        var resumed = Assert.Single(coordinator.GetUnreconciledAttempts([scenario.Goal.Id.Value]));
        Assert.Equal(attempt.AttemptId, resumed.AttemptId);
        Assert.Equal(attempt.FocusedEvidenceReceiptId, resumed.FocusedEvidenceReceiptId);
        Assert.Equal(1, launches);
        Assert.Empty(scenario.Roles);
        Assert.Empty(scenario.Requests);
    }

    [Theory(Timeout = TestHangGuard.WholeTestMilliseconds)]
    [Trait("Category", "CrossTick")]
    [InlineData(false)]
    [InlineData(true)]
    public void ResumedAttemptPreservesRejectedCliObligationsAndRequest(bool unresolvedDeclaration)
    {
        using var scenario = new Scenario("deferred - DeclaredCoreTests" +
            (unresolvedDeclaration ? ", MissingDeclaredTests" : ""));
        var oversized = new[] { "First", "Second" }.Select(prefix =>
            prefix + new string('X', GoalAcceptanceVerifier.MaxFocusedEvidenceFilterLength)).ToArray();
        for (var index = 0; index < oversized.Length; index++)
            scenario.DeclareCli(oversized[index], fileName: "Oversized" + index + ".cs");
        var launches = 0;
        var coordinator = scenario.Background(() => launches++);
        var driver = scenario.Driver(_ => throw new Xunit.Sdk.XunitException("Unexpected inline run"), coordinator);

        driver.AdvanceOnce(scenario.Goal, ConductorAutonomyPolicy.Conservative);

        var original = scenario.Latest();
        Assert.Equal("started", original.Outcome);
        Assert.Equal([CoreSelection, Cli(Golden), Cli(Writer)], original.Selections);
        Assert.Equal(oversized.Length + (unresolvedDeclaration ? 1 : 0), original.NotRun.Count);
        var attempt = Assert.Single(coordinator.GetUnreconciledAttempts([scenario.Goal.Id.Value]));
        Assert.Equal(1, launches);
        scenario.DeclareCli("NewlyDiscoveredTests");
        File.Delete(scenario.CliProjectPath); // Resume must use the recorded rejected obligations, too.
        var resumedDriver = scenario.Driver(_ => throw new Xunit.Sdk.XunitException("Unexpected inline run"),
            scenario.Background(() => launches++));

        var held = resumedDriver.AdvanceOnce(scenario.Goal, ConductorAutonomyPolicy.Conservative);

        Assert.IsType<ConductorAdvanceOutcome.Held>(held.Outcome);
        Assert.Equal(original.Selections, scenario.Latest().Selections);
        Assert.Equal(original.NotRun, scenario.Latest().NotRun);
        Assert.Equal((unresolvedDeclaration ? new[] { "MissingDeclaredTests" } : [])
            .Concat(oversized.Select(Cli)), scenario.Latest().NotRun);
        Assert.Equal(original.ReceiptId, scenario.Latest().ReceiptId);
        Assert.Equal("started", scenario.Latest().Outcome);
        var resumed = Assert.Single(coordinator.GetUnreconciledAttempts([scenario.Goal.Id.Value]));
        Assert.Equal(attempt.AttemptId, resumed.AttemptId);
        Assert.Equal(attempt.FocusedEvidenceReceiptId, resumed.FocusedEvidenceReceiptId);
        Assert.Equal(1, launches);
        Assert.Empty(scenario.Roles);
        Assert.Empty(scenario.Requests);
        Assert.Equal(0, scenario.GateRuns);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RecordedCliMembershipDoesNotMaskNotRunDrift(bool unresolvedDeclaration)
    {
        using var scenario = new Scenario("deferred - DeclaredCoreTests" +
            (unresolvedDeclaration ? ", MissingDeclaredTests" : ""));
        var original = new PreTesterEvidenceEntry("started", CandidateSha, "recorded-cli-receipt",
            [CoreSelection, Cli(Golden), Cli(Writer)], unresolvedDeclaration ? [] : ["UnexpectedTests"], null, []);
        scenario.Kernel.RecordFindingEvidenceRequest(scenario.Goal.Id, scenario.Tester.Id,
            PreTesterEvidenceIndexLines.FormatMarker(original));
        var driver = scenario.Driver(scenario.Green);

        var exception = Assert.Throws<InvalidDataException>(() =>
            driver.AdvanceOnce(scenario.Goal, ConductorAutonomyPolicy.Conservative));

        Assert.StartsWith("Started pre-Tester evidence selection changed before reconciliation.", exception.Message);
        Assert.Contains(unresolvedDeclaration
            ? "not_run_missing=<none> not_run_added=MissingDeclaredTests"
            : "not_run_missing=UnexpectedTests not_run_added=<none>", exception.Message, StringComparison.Ordinal);
        Assert.Equal("started", scenario.Latest().Outcome);
        Assert.Equal(original.Selections, scenario.Latest().Selections);
        Assert.Equal(original.NotRun, scenario.Latest().NotRun);
        Assert.Equal(original.ReceiptId, scenario.Latest().ReceiptId);
        Assert.Empty(scenario.Requests);
        Assert.Empty(scenario.Roles);
        Assert.Equal(0, scenario.GateRuns);
    }

    [Fact(Timeout = TestHangGuard.WholeTestMilliseconds)]
    [Trait("Category", "CrossTick")]
    public void SupersedingRefreshDiscoversNewMembershipThenFreezesIt()
    {
        using var scenario = new Scenario();
        var first = scenario.Driver(BuildOnlyUnusable).AdvanceOnce(scenario.Goal, ConductorAutonomyPolicy.Conservative);
        Assert.IsType<ConductorAdvanceOutcome.Held>(first.Outcome);
        var old = scenario.Latest();
        Assert.Equal("unusable", old.Outcome);
        Assert.DoesNotContain(Cli("NewBehaviorTests"), old.Selections);
        scenario.DeclareCli("NewBehaviorTests");
        var launches = 0;
        var coordinator = scenario.Background(() => launches++);
        var driver = scenario.Driver(_ => throw new Xunit.Sdk.XunitException("Unexpected inline run"), coordinator);

        driver.AdvanceOnce(scenario.Goal, ConductorAutonomyPolicy.Conservative);

        var fresh = scenario.Latest();
        Assert.Equal("started", fresh.Outcome);
        Assert.Contains(Cli("NewBehaviorTests"), fresh.Selections);
        Assert.NotEqual(old.ReceiptId, fresh.ReceiptId);
        var refresh = Assert.Single(scenario.Goal.Timeline.Select(evt =>
            PreTesterEvidenceRefreshRule.TryParse(evt.Message, out var marker) ? marker : null).OfType<PreTesterEvidenceRefreshMarker>());
        Assert.Equal(old.ReceiptId, refresh.SupersededReceiptId);
        Assert.Equal(fresh.ReceiptId, refresh.FreshReceiptId);
        scenario.DeclareCli("LaterTests");
        driver.AdvanceOnce(scenario.Goal, ConductorAutonomyPolicy.Conservative);
        Assert.Equal(fresh.Selections, scenario.Latest().Selections);
        Assert.Equal(fresh.ReceiptId, scenario.Latest().ReceiptId);
        Assert.Equal("started", scenario.Latest().Outcome);
        Assert.Equal(1, launches);
        Assert.Single(scenario.Requests);
        Assert.Empty(scenario.Roles);
    }

    [Fact]
    public void OverflowPackingAndIncompleteExecutionKeepEveryCliObligationNotRun()
    {
        using var scenario = new Scenario();
        for (var i = 0; i < 8; i++) scenario.DeclareCli("Wide" + i + new string('A', 220) + "Tests");
        var guards = PreTesterGuardSelections.Create(new(), scenario.Worktree, [ChangedPath], "base",
            _ => {}, scenario.Settings);
        var normalized = guards.NormalizeWithNotRun(scenario.Settings);
        var selected = guards.RetainStartedSelections(normalized.Selections, null, ConductorDriver.FormatFindingEvidenceSelection);
        Assert.Equal(10, selected.Length);
        Assert.Empty(normalized.NotRun);
        var request = string.Join("; ", selected.Select(ConductorDriver.FormatFindingEvidenceSelection));
        Assert.True(GoalAcceptanceVerifier.TryBuildFocusedEvidenceChecks(request, scenario.Settings, scenario.Worktree,
            out var checks, out var coverage, out var rejection), rejection.Detail);
        Assert.True(checks.Count > 1);
        Assert.Equal(selected.Length, coverage.TargetToChecks.Count);
        Assert.All(checks, check =>
        {
            var args = check.Arguments.ToArray();
            var index = Array.IndexOf(args, "--filter");
            Assert.InRange(index, 0, args.Length - 2);
            Assert.InRange(args[index + 1].Length, 1, GoalAcceptanceVerifier.MaxFocusedEvidenceFilterLength);
        });
        var notRun = FocusedEvidenceNotRunPlan.Compare(checks, [], coverage, continueAfterFailure: false);
        Assert.Equal(selected.Select(ConductorDriver.FormatFindingEvidenceSelection).Order(StringComparer.Ordinal),
            notRun.Selections.Select(ConductorDriver.FormatFindingEvidenceSelection).Order(StringComparer.Ordinal));
        Assert.All(notRun.Checks, check => Assert.Equal("not-run:no-result-recorded", check.Reason));
    }

    [Fact]
    public void IncompleteCliReceiptCannotClaimGreenCoverage()
    {
        using var scenario = new Scenario();
        var driver = scenario.Driver(request => ConductorDriverTestsFindingEvidenceReuse.RetainedEvidenceWithExecutedClasses(
            scenario.Root, request, CandidateSha, ["DeclaredCoreTests", Golden]));
        driver.AdvanceOnce(scenario.Goal, ConductorAutonomyPolicy.Conservative);
        Assert.Equal([CoreSelection, Cli(Golden), Cli(Writer)], Selections(Assert.Single(scenario.Requests)));
        Assert.Equal("unusable", scenario.Latest().Outcome);
        Assert.Contains(Cli(Writer), scenario.Latest().Selections);
        Assert.Empty(scenario.Feedback);
        Assert.Equal(0, scenario.GateRuns);
    }

    [Fact]
    public void NewClassesStayInRemainderWithoutExpandingProcessSpawningLane()
    {
        var settings = AcceptanceGateEngineSettings.Load(VerifiedRepositoryRoot.Find());
        var classes = new[] { typeof(PreTesterCliSurfaceWiringTests), typeof(PreTesterCliSurfaceSelectionTests) }
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

    private static string[] Selections(string request) => request.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    private static FocusedEvidenceRunResult CandidateOnlyRed(string request, string failure)
    {
        var red = CandidateRedFindingEvidence(request, CandidateSha, failure);
        var candidate = red.Arms!.Single(arm => arm.Arm == FindingEvidenceArm.Candidate);
        return red with { Checks = candidate.Checks, Arms = [candidate] };
    }
    private static FocusedEvidenceRunResult BuildOnlyUnusable(string request)
    {
        var check = new AcceptanceCheckResult("build", false, -1, "no tests executed",
            FailureClassification: AcceptanceFailureClassifications.BuildPhaseTimeout, ExecutedTestCount: 0);
        return new(request, true, false, "build timed out", [],
            Arms: [new(FindingEvidenceArm.Candidate, CandidateSha, FindingEvidenceArmDisposition.Inconclusive,
                true, false, "build timed out", [check])]);
    }

    private sealed class Scenario : IDisposable
    {
        public string Root { get; } = ConductorDriverTests.CreateTempDirectory();
        public AgentOrchestratorKernel Kernel { get; }
        public Goal Goal { get; }
        public TaskSpec Developer { get; }
        public TaskSpec Tester { get; }
        public string Worktree => GoalWorktrees.WorktreePath(Root, Goal.Id);
        public string CliProjectPath => Path.Combine(Worktree, PreTesterCliSurfaceSelections.Project);
        public string CliDirectory => Path.GetDirectoryName(CliProjectPath)!;
        public IReadOnlyList<string> Paths { get; set; } = [ChangedPath];
        public List<string> Requests { get; } = [];
        public List<AgentRole> Roles { get; } = [];
        public List<string> Feedback { get; } = [];
        public int GateRuns { get; private set; }
        public AcceptanceGateEngineSettings Settings { get; } = new()
        {
            MtpInvocations = [new AcceptanceMtpInvocation { Project = PreTesterCliSurfaceSelections.Project }]
        };
        public Scenario(string tests = "deferred - DeclaredCoreTests")
        {
            (Kernel, Goal) = SoftwareGoal("Pre-Tester CLI behavior evidence");
            Developer = Goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
            Tester = Goal.Tasks.Single(task => task.RequiredRole == AgentRole.Tester);
            foreach (var task in Goal.Tasks.TakeWhile(task => task.Id != Developer.Id)) PassVerification(Kernel, Goal, task);
            DispatchTask(Kernel, Goal, Developer, baseCommit: "aaa1111");
            var output = string.Join(Environment.NewLine, "WORKER_RESULT:", "files: " + ChangedPath, "commands: none",
                "tests: " + tests, "commit: none", "blockers: none", "assigned_scope_complete: true",
                "model_fit: test/model - adequate - fixture", "skills: none", "confidence: high", "END_WORKER_RESULT");
            Kernel.RecordTaskVerification(Goal.Id, Developer.Id, new("fixture", Root, 0, output, "", DateTimeOffset.UtcNow,
                WorkerResultPresent: true, HasCommittedChanges: true));
            Directory.CreateDirectory(Worktree);
            File.WriteAllText(Path.Combine(Worktree, ".git"), "gitdir: fixture");
            var core = Path.Combine(Worktree, "tests", "Mcg.AgentOrchestrator.Core.Tests");
            Directory.CreateDirectory(core);
            File.WriteAllText(Path.Combine(core, "Mcg.AgentOrchestrator.Core.Tests.csproj"), "<Project />");
            File.WriteAllText(Path.Combine(core, "DeclaredCoreTests.cs"), "public class DeclaredCoreTests { [Xunit.Fact] public void Runs() {} }");
            DeclareCli(Golden);
            DeclareCli(Writer);
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(CliDirectory)!, "Mcg.AgentOrchestrator.Infrastructure.Tests.csproj"),
                "<Project><ItemGroup><Compile Remove=\"Cli/**/*.cs\" /></ItemGroup></Project>");
        }
        public void DeclareCli(string name, string methods = "[Fact] public void Output() {}", string? fileName = null)
        {
            Directory.CreateDirectory(CliDirectory);
            File.WriteAllText(CliProjectPath, "<Project />");
            File.WriteAllText(Path.Combine(CliDirectory, fileName ?? name + ".cs"), "public class " + name + " { " + methods + " }");
        }
        public PreTesterEvidenceEntry Latest() => Assert.IsType<PreTesterEvidenceEntry>(
            PreTesterEvidenceIndexLines.Latest(Goal, Tester.Id, CandidateSha));
        public FocusedEvidenceRunResult Green(string request) =>
            ConductorDriverTestsFindingEvidenceReuse.RetainedEvidenceWithExecutedClasses(Root, request, CandidateSha);
        public ConductorParallelAcceptanceAttemptCoordinator Background(Action launched) => new(
            Path.Combine(Root, "attempts"), TrunkBranchName.Default, isProcessAlive: pid => pid == 7103,
            launchOwnedProcess: _ => { launched(); return new(7103); }, acquireStableSlotLease: (_, _) => null);
        public ConductorDriver Driver(Func<string, FocusedEvidenceRunResult> run,
            ConductorParallelAcceptanceAttemptCoordinator? coordinator = null)
        {
            var driver = MakeDriver(getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
                getPreReviewEvidenceContext: _ => NoPreReviewContext(CandidateSha), getLandingFileScopes: _ => Paths,
                getFindingEvidenceEngineSettings: _ => Settings,
                focusedEvidenceAttemptCoordinator: coordinator ?? new(Path.Combine(Root, "attempts"), TrunkBranchName.Default,
                    runInline: true, acquireStableSlotLease: (_, _) => null),
                runFocusedEvidence: (_, request) => { Requests.Add(request); return run(request); },
                runAcceptance: _ => { GateRuns++; return true; },
                runAcceptanceVerificationWithLease: (_, _, _, _) => { GateRuns++; return AcceptanceVerificationSummary.PassedWithNoUnmetCriteria; },
                dispatchAndStart: goal =>
                {
                    Roles.Add(goal.Tasks.First(task => task.Status == WorkTaskStatus.Assigned).RequiredRole);
                    return DispatchStartOutcome.Started();
                },
                retryTaskWithCause: (goalId, taskId, message, round, cause) =>
                {
                    Feedback.Add(message);
                    return Kernel.RetryTask(goalId, taskId, message, retryRoundKind: round, retryCause: cause);
                },
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
