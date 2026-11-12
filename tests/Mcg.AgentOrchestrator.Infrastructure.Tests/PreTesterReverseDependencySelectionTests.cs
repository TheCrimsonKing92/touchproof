using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using static ConductorDriverTests;
using Fixture = PreTesterCoupledClassSelectionsTests.Fixture;

// Parallel-safe: private candidate files/kernel/attempts; process launch and evidence execution are seamed.
public sealed class PreTesterReverseDependencySelectionTests
{
    private const string AppSource = "src/Mcg.AgentOrchestrator.App/Orchestration/AppContract.cs";
    private const string CandidateSha = "bbb2222";
    private const string Consumer = "Infrastructure.Tests:ConsumerTests";
    private const string Declared = "Core.Tests:DeclaredCoreTests";
    private const string Failure = "ConsumerTests.Works";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AppOrchestrationChangeSelectsEveryReverseDependentClass(bool companions)
    {
        using var fixture = Repository();
        fixture.Test("SecondConsumerTests", "AppContract");
        string[] paths = companions ? [AppSource, "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/UnrelatedTests.cs",
            "scripts/check.ps1", "config/settings.json"] : [AppSource];
        fixture.Write("scripts/check.ps1", "# fixture");
        fixture.Write("config/settings.json", "{}");
        var expected = RepositoryRecheckTestSelection.Read(fixture.Root, [AppSource]);
        Assert.True(expected.Resolved, expected.Reason);
        Assert.Equal(["ConsumerTests", "SecondConsumerTests"], expected.TestClasses);
        Assert.Empty(PreTesterCoupledClassSelections.Select(fixture.Root, paths, fixture.Settings, _ => { }).Selections);
        var lines = new List<string>();

        var guards = Select(fixture, paths, lines.Add);
        var selected = guards.Augment(new([], []), fixture.Settings, null, ConductorDriver.FormatFindingEvidenceSelection);

        Assert.Equal(expected.TestClasses, selected.Selections.Select(item => item.TestClass).Order());
        Assert.All(selected.Selections, item => Assert.Equal("Infrastructure.Tests", item.TestProject));
        foreach (var name in expected.TestClasses)
            Assert.Single(lines, line => line == $"PRE_TESTER_GUARD_SELECTED class={name} kind=reverse-dependency path={AppSource}");
        Assert.DoesNotContain(selected.Selections, item => item.TestClass == "UnrelatedTests");
        Assert.Empty(selected.NotRun);
        // Overlap with a declared class creates one executable obligation and keeps attribution.
        var overlap = guards.Augment(new([new("Infrastructure.Tests", "ConsumerTests")], []), fixture.Settings,
            null, ConductorDriver.FormatFindingEvidenceSelection);
        Assert.Single(overlap.Selections.Where(item => item.TestClass == "ConsumerTests"));
    }

    [Theory]
    [InlineData("unreadable")]
    [InlineData("abandoned")]
    [InlineData("missing-path")]
    [InlineData("non-csharp")]
    [InlineData("missing-class")]
    [InlineData("ambiguous-class")]
    public void DegradedOrUnresolvedSelectionAddsNothing(string condition)
    {
        using var fixture = Repository();
        string[] paths = [AppSource];
        Func<string, IReadOnlyList<string>, RepositoryRecheckTestSelection>? reader = null;
        switch (condition)
        {
            case "unreadable":
                fixture.Write(AppSource, "public class Broken {");
                Assert.Equal(ReverseDependencySelectionOutcome.Unreadable,
                    ReverseDependencyTestImpactReader.Read(fixture.Root, paths).Outcome);
                break;
            case "abandoned":
                reader = (_, _) => new(false, ["ConsumerTests"], "Abandoned:fixture-bound");
                break;
            case "missing-path":
                paths = [AppSource, "src/Mcg.AgentOrchestrator.App/Orchestration/Missing.cs"];
                Assert.False(File.Exists(Path.Combine(fixture.Root, paths[1])));
                break;
            case "non-csharp":
                const string vb = "src/Mcg.AgentOrchestrator.App/Orchestration/Other.vb";
                fixture.Write(vb, "Public Class Other\nEnd Class");
                paths = [AppSource, vb];
                break;
            case "missing-class":
                reader = (_, _) => new(true, ["ConsumerTests", "AbsentTests"]);
                break;
            case "ambiguous-class":
                fixture.Write("tests/Mcg.AgentOrchestrator.Infrastructure.Tests/OtherConsumer.cs", """
                    namespace Other {
                        public class ConsumerTests { [Fact] public void Works() { _ = new AppContract(); } }
                    }
                    """);
                var result = RepositoryRecheckTestSelection.Read(fixture.Root, paths);
                Assert.True(result.Resolved, result.Reason);
                Assert.Contains("ConsumerTests", result.TestClasses);
                Assert.Equal(2, AcceptanceTestClassSourceScanner.ScanProject(fixture.Root, Fixture.Project)
                    .Count(item => item.FullName == "ConsumerTests" || item.FullName.EndsWith(".ConsumerTests", StringComparison.Ordinal)));
                break;
        }
        var declaration = new DeveloperDeferredTestSelection([new("Infrastructure.Tests", "UnrelatedTests")], []);
        var baseline = Select(fixture, paths, _ => { }, (_, _) => new(true, []))
            .Augment(declaration, fixture.Settings, null, ConductorDriver.FormatFindingEvidenceSelection);
        var lines = new List<string>();

        var actual = Select(fixture, paths, lines.Add, reader)
            .Augment(declaration, fixture.Settings, null, ConductorDriver.FormatFindingEvidenceSelection);

        Assert.Equal(baseline.Selections, actual.Selections);
        Assert.Equal(baseline.NotRun, actual.NotRun);
        Assert.Contains(new FindingEvidenceSelection("Infrastructure.Tests", "UnrelatedTests"), actual.Selections);
        Assert.DoesNotContain(actual.Selections, item => item.TestClass == "ConsumerTests");
        Assert.Empty(actual.NotRun);
        Assert.DoesNotContain(lines, line => line.Contains("kind=reverse-dependency path=", StringComparison.Ordinal));
        var diagnostic = Assert.Single(lines.Where(line => line.Contains("kind=reverse-dependency-", StringComparison.Ordinal)));
        Assert.Contains("kind=reverse-dependency-" + (condition.EndsWith("class", StringComparison.Ordinal) ? "unresolved" : "degraded"),
            diagnostic, StringComparison.Ordinal);
        Assert.Contains("reason=", diagnostic, StringComparison.Ordinal);
        Assert.Contains("path=" + AppSource, diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public void FailingReverseDependencyClassIsActionableRedAndRetriesDeveloper()
    {
        using var scenario = new Scenario();
        scenario.Repository.Test("CoupledOnlyTests", prefix: "[LiveTreeStructural]");
        var guards = Select(scenario.Repository, [AppSource], _ => { });
        Assert.Contains(new FindingEvidenceSelection("Infrastructure.Tests", "CoupledOnlyTests"), guards.Selections);
        Assert.True(guards.EveryFailureIsInsideChanges([Failure], _ => throw new InvalidOperationException("Unexpected outside failure")));
        foreach (var classification in new[] { false, true })
        {
            var calls = 0;
            Assert.Equal(classification, guards.EveryFailureIsInsideChanges([Failure, "OutsideTests.Fails"], outside =>
            {
                calls++;
                Assert.Equal(["OutsideTests.Fails"], outside);
                return classification;
            }));
            Assert.Equal(1, calls);
        }
        Assert.False(guards.EveryFailureIsInsideChanges(["CoupledOnlyTests.Fails"], _ => false));
        var outsideEvidence = CandidateRedFindingEvidence(Declared, CandidateSha, "OutsideTests.Fails");
        var outsideArm = outsideEvidence.Arms!.Single(arm => arm.Arm == FindingEvidenceArm.Candidate);
        var outsideOutcome = PreTesterApparatusReceiptClassifier.ClassifyOutcome(
            outsideEvidence with { Checks = outsideArm.Checks, Arms = [outsideArm] }, true, CandidateSha, guards.Selections,
            failures => guards.EveryFailureIsInsideChanges(failures, _ => false));
        Assert.Equal("red", outsideOutcome.Outcome);
        Assert.Equal(["OutsideTests.Fails"], outsideOutcome.FailingTests);

        scenario.Driver(red: true).AdvanceOnce(scenario.Goal, ConductorAutonomyPolicy.Conservative);

        Assert.Contains(Consumer, Assert.Single(scenario.Requests), StringComparison.Ordinal);
        Assert.Equal("actionable-red", scenario.Latest().Outcome);
        Assert.Equal([Failure], scenario.Latest().FailingTests);
        var feedback = Assert.Single(scenario.Feedback);
        Assert.StartsWith("ACTIONABLE_CANDIDATE_RED", feedback, StringComparison.Ordinal);
        Assert.Contains(Failure, feedback, StringComparison.Ordinal);
        Assert.Equal([AgentRole.Developer], scenario.Roles);
        Assert.Equal(0, scenario.GateRuns);
    }

    [Theory(Timeout = TestHangGuard.WholeTestMilliseconds)]
    [Trait("Category", "CrossTick")]
    [InlineData("more")]
    [InlineData("fewer")]
    [InlineData("degraded")]
    public void ResumedStartedAttemptKeepsRecordedSelection(string recomputation)
    {
        using var scenario = new Scenario();
        var launches = 0;
        var coordinator = scenario.Background(() => launches++);
        scenario.Driver(coordinator: coordinator).AdvanceOnce(scenario.Goal, ConductorAutonomyPolicy.Conservative);
        var original = scenario.Latest();
        Assert.Equal("started", original.Outcome);
        Assert.Equal([Declared, Consumer], original.Selections);
        var attempt = Assert.Single(coordinator.GetUnreconciledAttempts([scenario.Goal.Id.Value]));
        switch (recomputation)
        {
            case "more": scenario.Repository.Test("NewConsumerTests", "AppContract"); break;
            case "fewer": File.Delete(Path.Combine(scenario.Repository.Root,
                "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/ConsumerTests.cs")); break;
            case "degraded": scenario.Repository.Write(AppSource, "public class Broken {"); break;
        }
        var recomputed = RepositoryRecheckTestSelection.Read(scenario.Repository.Root, [AppSource]);
        Assert.Equal(recomputation != "degraded", recomputed.Resolved);
        if (recomputation == "more") Assert.Equal(["ConsumerTests", "NewConsumerTests"], recomputed.TestClasses);
        if (recomputation == "fewer") Assert.Empty(recomputed.TestClasses);

        var result = scenario.Driver(coordinator: scenario.Background(() => launches++))
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

    private static Fixture Repository(string? root = null)
    {
        var fixture = new Fixture(root);
        fixture.Write("src/Mcg.AgentOrchestrator.App/Mcg.AgentOrchestrator.App.csproj", "<Project />");
        fixture.Write(Fixture.Project, """
            <Project><ItemGroup>
              <ProjectReference Include="../../src/Mcg.AgentOrchestrator.Core/Mcg.AgentOrchestrator.Core.csproj" />
              <ProjectReference Include="../../src/Mcg.AgentOrchestrator.App/Mcg.AgentOrchestrator.App.csproj" />
            </ItemGroup></Project>
            """);
        fixture.Write(AppSource, "public class AppContract {}");
        fixture.Test("ConsumerTests", "AppContract");
        fixture.Test("UnrelatedTests");
        return fixture;
    }

    private static PreTesterGuardSelections Select(Fixture fixture, IReadOnlyList<string> paths, Action<string> progress,
        Func<string, IReadOnlyList<string>, RepositoryRecheckTestSelection>? reader = null) =>
        PreTesterGuardSelections.Create(new(), fixture.Root, paths, "fixture-main", progress, fixture.Settings,
            structuralPathReader: (_, _) => [], reverseDependencySelector: reader);

    private sealed class Scenario : IDisposable
    {
        internal string Root { get; } = ConductorDriverTests.CreateTempDirectory();
        internal AgentOrchestratorKernel Kernel { get; }
        internal Goal Goal { get; }
        private TaskSpec Tester { get; }
        internal Fixture Repository { get; }
        internal List<string> Requests { get; } = [];
        internal List<string> Feedback { get; } = [];
        internal List<AgentRole> Roles { get; } = [];
        internal int GateRuns { get; private set; }

        internal Scenario()
        {
            (Kernel, Goal) = SoftwareGoal("Pre-Tester reverse dependency evidence");
            var developer = Goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
            Tester = Goal.Tasks.Single(task => task.RequiredRole == AgentRole.Tester);
            foreach (var task in Goal.Tasks.TakeWhile(task => task.Id != developer.Id)) PassVerification(Kernel, Goal, task);
            DispatchTask(Kernel, Goal, developer, baseCommit: "aaa1111");
            var output = string.Join(Environment.NewLine, "WORKER_RESULT:", "files: " + AppSource, "commands: none",
                "tests: deferred - DeclaredCoreTests", "commit: none", "blockers: none", "assigned_scope_complete: true",
                "model_fit: test/model - adequate - fixture", "skills: none", "confidence: high", "END_WORKER_RESULT");
            Kernel.RecordTaskVerification(Goal.Id, developer.Id, new("fixture", Root, 0, output, "", DateTimeOffset.UtcNow,
                WorkerResultPresent: true, HasCommittedChanges: true));
            Repository = PreTesterReverseDependencySelectionTests.Repository(GoalWorktrees.WorktreePath(Root, Goal.Id));
            Repository.Write("tests/Mcg.AgentOrchestrator.Core.Tests/DeclaredCoreTests.cs", "public class DeclaredCoreTests {}");
        }

        internal PreTesterEvidenceEntry Latest() => Assert.IsType<PreTesterEvidenceEntry>(
            PreTesterEvidenceIndexLines.Latest(Goal, Tester.Id, CandidateSha));

        internal ConductorParallelAcceptanceAttemptCoordinator Background(Action launched) => new(
            Path.Combine(Root, "attempts"), TrunkBranchName.Default, isProcessAlive: pid => pid == 7103,
            launchOwnedProcess: _ => { launched(); return new(7103); }, acquireStableSlotLease: (_, _) => null);

        internal ConductorDriver Driver(bool red = false, ConductorParallelAcceptanceAttemptCoordinator? coordinator = null)
        {
            var driver = MakeDriver(getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
                getPreReviewEvidenceContext: _ => NoPreReviewContext(CandidateSha), getLandingFileScopes: _ => [AppSource],
                getFindingEvidenceEngineSettings: _ => Repository.Settings,
                focusedEvidenceAttemptCoordinator: coordinator ?? new(Path.Combine(Root, "attempts"), TrunkBranchName.Default,
                    runInline: true, acquireStableSlotLease: (_, _) => null),
                runFocusedEvidence: (_, request) =>
                {
                    Requests.Add(request);
                    if (!red) return ConductorDriverTestsFindingEvidenceReuse.RetainedEvidenceWithExecutedClasses(Root, request, CandidateSha);
                    var evidence = CandidateRedFindingEvidence(request, CandidateSha, Failure);
                    var candidate = evidence.Arms!.Single(arm => arm.Arm == FindingEvidenceArm.Candidate);
                    return evidence with { Checks = candidate.Checks, Arms = [candidate] };
                },
                runAcceptance: _ => { GateRuns++; return true; },
                runAcceptanceVerificationWithLease: (_, _, _, _) =>
                {
                    GateRuns++;
                    return AcceptanceVerificationSummary.PassedWithNoUnmetCriteria;
                },
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
