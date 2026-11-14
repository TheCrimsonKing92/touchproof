using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

using static ConductorDriverTests;

// Parallel-safe: all files and coordinator state belong to this scenario; process launch is seamed.
public sealed class ConductorDriverTestsPreTesterAlwaysRunGuards
{
    private const string CandidateSha = "bbb2222";
    private const string BaseSha = "aaa1111";
    private const string CoreSelection = "Core.Tests:DeclaredCoreTests";
    private const string ChangedCorePath = "tests/Mcg.AgentOrchestrator.Core.Tests/DeclaredCoreTests.cs";
    private static string[] GuardNames => PreTesterAlwaysRunGuardTestClasses.Entries
        .Select(entry => entry.TestClass).ToArray();
    private static string[] GuardSelections => GuardNames.Select(name => "Infrastructure.Tests:" + name).ToArray();

    [Fact]
    public void DeclaredCoreClassPrecedesAllSixInfrastructureGuards()
    {
        using var scenario = new Scenario();
        var roles = new List<AgentRole>();
        var launches = 0;
        var coordinator = new ConductorParallelAcceptanceAttemptCoordinator(
            Path.Combine(scenario.Root, "background-attempts"), Mcg.AgentOrchestrator.Infrastructure.TrunkBranchName.Default,
            isProcessAlive: id => id == 7103,
            launchOwnedProcess: _ =>
            {
                launches++;
                return new ConductorParallelAcceptanceOwnedProcessLaunchResult(7103);
            },
            acquireStableSlotLease: (_, _) => null);
        var driver = scenario.Driver(
            _ => throw new Xunit.Sdk.XunitException("Background evidence must not execute inline."),
            roles.Add, coordinator: coordinator);

        driver.AdvanceOnce(scenario.Goal, ConductorAutonomyPolicy.Conservative);

        var started = scenario.Latest();
        Assert.Equal("started", started.Outcome);
        Assert.Equal(new[] { CoreSelection }.Concat(GuardSelections), started.Selections);
        Assert.Equal(10, started.Selections.Count);
        Assert.Empty(started.NotRun);
        Assert.Equal(1, launches);
        Assert.Empty(roles);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ListedFailureOutsideChangesRetriesDeveloperWithFailureDetail(bool hasChangedPaths)
    {
        using var scenario = new Scenario();
        var identity = GuardNames[0] + ".FailsOnCandidate";
        var trxPath = Path.Combine(scenario.Root, "guard-failure.trx");
        ConductorDriverTestsActionableRedFailureDetail.WriteFailureTrx(trxPath, identity);
        var roles = new List<AgentRole>();
        var feedback = new List<string>();
        var runs = 0;
        var driver = scenario.Driver(request =>
        {
            runs++;
            return CandidateOnlyRed(request, [identity], trxPath);
        }, roles.Add, feedback.Add, changedPaths: hasChangedPaths ? [ChangedCorePath] : []);

        driver.AdvanceOnce(scenario.Goal, ConductorAutonomyPolicy.Conservative);

        Assert.Equal(1, runs);
        Assert.Equal("actionable-red", scenario.Latest().Outcome);
        Assert.Equal([identity], scenario.Latest().FailingTests);
        Assert.Equal([AgentRole.Developer], roles);
        var message = Assert.Single(feedback);
        Assert.StartsWith("ACTIONABLE_CANDIDATE_RED", message, StringComparison.Ordinal);
        Assert.Contains(identity, message, StringComparison.Ordinal);
        Assert.Contains("fatal: synthetic too big", message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnlistedOutsideFailureKeepsEvenMixedFailuresPlainRed(bool alsoListedFailure)
    {
        using var scenario = new Scenario(tests: "deferred - DeclaredCoreTests, UnlistedOutsideTests");
        scenario.Declare("UnlistedOutsideTests");
        var failures = new List<string> { "UnlistedOutsideTests.FailsOnCandidate" };
        if (alsoListedFailure) failures.Add(GuardNames[0] + ".FailsOnCandidate");
        var roles = new List<AgentRole>();
        var feedback = new List<string>();
        var runs = 0;
        var driver = scenario.Driver(request =>
        {
            runs++;
            return CandidateOnlyRed(request, failures);
        }, roles.Add, feedback.Add);

        driver.AdvanceOnce(scenario.Goal, ConductorAutonomyPolicy.Conservative);

        Assert.Equal(1, runs);
        Assert.Equal("red", scenario.Latest().Outcome);
        Assert.Equal(failures, scenario.Latest().FailingTests);
        Assert.Equal([AgentRole.Tester], roles);
        Assert.Empty(feedback);
    }

    [Fact]
    public void ListedAndChangedSourceFailuresAreBothActionable()
    {
        using var scenario = new Scenario();
        var failures = new[] { GuardNames[0] + ".FailsOnCandidate", "DeclaredCoreTests.FailsOnCandidate" };
        var roles = new List<AgentRole>();
        var feedback = new List<string>();
        var driver = scenario.Driver(request => CandidateOnlyRed(request, failures), roles.Add, feedback.Add);

        driver.AdvanceOnce(scenario.Goal, ConductorAutonomyPolicy.Conservative);

        Assert.Equal("actionable-red", scenario.Latest().Outcome);
        Assert.Equal([AgentRole.Developer], roles);
        var message = Assert.Single(feedback);
        Assert.All(failures, identity => Assert.Contains(identity, message, StringComparison.Ordinal));
    }

    [Fact]
    public void MissingGuardIsOmittedWithoutNotRunAndEvidenceStillStarts()
    {
        var missing = GuardNames[2];
        using var scenario = new Scenario(missingGuard: missing);
        var requests = new List<string>();
        var roles = new List<AgentRole>();
        var driver = scenario.Driver(request =>
        {
            requests.Add(request);
            return scenario.Green(request);
        }, roles.Add);

        driver.AdvanceOnce(scenario.Goal, ConductorAutonomyPolicy.Conservative);

        var expected = new[] { CoreSelection }.Concat(GuardSelections.Where(selection =>
            selection != "Infrastructure.Tests:" + missing)).ToArray();
        Assert.Equal(expected, Selections(Assert.Single(requests)));
        var receipt = scenario.Latest();
        Assert.Equal("green", receipt.Outcome);
        Assert.Equal(expected, receipt.Selections);
        Assert.Empty(receipt.NotRun);
        Assert.DoesNotContain("Infrastructure.Tests:" + missing, receipt.Selections);
        Assert.Equal([AgentRole.Tester], roles);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LegacyStartedSelectionRemainsAuthoritativeOnReconciliation(bool includesOneGuard)
    {
        using var scenario = new Scenario();
        var startedSelections = includesOneGuard ? new[] { CoreSelection, GuardSelections[0] } : [CoreSelection];
        scenario.RecordStarted(startedSelections);
        var requests = new List<string>();
        var roles = new List<AgentRole>();
        var driver = scenario.Driver(request =>
        {
            requests.Add(request);
            return scenario.Green(request);
        }, roles.Add);

        var exception = Record.Exception(() => driver.AdvanceOnce(scenario.Goal, ConductorAutonomyPolicy.Conservative));

        Assert.Null(exception);
        Assert.Equal(startedSelections, Selections(Assert.Single(requests)));
        var receipt = scenario.Latest();
        Assert.Equal("green", receipt.Outcome);
        Assert.Equal(startedSelections, receipt.Selections);
        Assert.Empty(receipt.NotRun);
        Assert.Equal([AgentRole.Tester], roles);
    }

    [Fact]
    public void StartedReceiptAcceptsReorderedRecordedSelections()
    {
        using var scenario = new Scenario();
        var recorded = new[] { GuardSelections[0], CoreSelection };
        scenario.RecordStarted(recorded);
        var requests = new List<string>();
        var roles = new List<AgentRole>();
        var driver = scenario.Driver(request =>
        {
            requests.Add(request);
            return scenario.Green(request);
        }, roles.Add);

        var exception = Record.Exception(() => driver.AdvanceOnce(scenario.Goal, ConductorAutonomyPolicy.Conservative));

        Assert.Null(exception);
        Assert.Equal(recorded, Selections(Assert.Single(requests)));
        Assert.Equal("green", scenario.Latest().Outcome);
        Assert.Equal(recorded, scenario.Latest().Selections);
        Assert.Equal([AgentRole.Tester], roles);
    }

    [Fact]
    public void StartedReceiptMissingRecordedClassRunsRecordedSelection()
    {
        using var scenario = new Scenario();
        const string missing = "Core.Tests:RemovedCoreTests";
        var recorded = new[] { CoreSelection, missing };
        scenario.RecordStarted(recorded);
        var requests = new List<string>();
        var driver = scenario.Driver(request =>
        {
            requests.Add(request);
            return scenario.Green(request);
        }, _ => { });

        var exception = Record.Exception(() =>
            driver.AdvanceOnce(scenario.Goal, ConductorAutonomyPolicy.Conservative));

        Assert.Null(exception);
        Assert.Equal(recorded, Selections(Assert.Single(requests)));
        Assert.Equal("green", scenario.Latest().Outcome);
        Assert.Equal(recorded, scenario.Latest().Selections);
        Assert.Empty(scenario.Latest().NotRun);
    }

    [Theory]
    [InlineData("selection")]
    [InlineData("not-run")]
    public void StartedReceiptKeepsRecordingDespiteUnrelatedDrift(string drift)
    {
        using var scenario = new Scenario();
        var selections = drift switch
        {
            "selection" => new[] { "Core.Tests:OtherCoreTests" },
            _ => new[] { CoreSelection }
        };
        string[] notRun = drift == "not-run" ? ["MissingTests"] : [];
        scenario.RecordStarted(selections, notRun);
        var requests = new List<string>();
        var driver = scenario.Driver(request =>
        {
            requests.Add(request);
            return scenario.Green(request);
        }, _ => { });

        var exception = Record.Exception(() =>
            driver.AdvanceOnce(scenario.Goal, ConductorAutonomyPolicy.Conservative));

        Assert.Null(exception);
        Assert.Equal(selections, Selections(Assert.Single(requests)));
        Assert.Equal("green", scenario.Latest().Outcome);
        Assert.Equal(selections, scenario.Latest().Selections);
        Assert.Equal(notRun, scenario.Latest().NotRun);
    }

    [Fact]
    public void AlreadyDeclaredGuardKeepsItsEarlierPositionWithoutDuplicate()
    {
        using var scenario = new Scenario(tests: "deferred - " + GuardNames[3] + ", DeclaredCoreTests");
        var requests = new List<string>();
        var driver = scenario.Driver(request =>
        {
            requests.Add(request);
            return scenario.Green(request);
        }, _ => { });

        driver.AdvanceOnce(scenario.Goal, ConductorAutonomyPolicy.Conservative);

        Assert.Equal(new[] { GuardSelections[3], CoreSelection }
            .Concat(GuardSelections.Where(selection => selection != GuardSelections[3])),
            Selections(Assert.Single(requests)));
        Assert.Equal("green", scenario.Latest().Outcome);
    }

    [Fact]
    public void SelectedGuardWithoutContentBoundTrxPreventsGreen()
    {
        using var scenario = new Scenario();
        var runs = 0;
        var driver = scenario.Driver(request =>
        {
            runs++;
            return ConductorDriverTestsFindingEvidenceReuse.RetainedEvidenceWithExecutedClasses(
                scenario.Root, request, CandidateSha, ["DeclaredCoreTests"]);
        }, _ => { });

        driver.AdvanceOnce(scenario.Goal, ConductorAutonomyPolicy.Conservative);

        Assert.Equal(1, runs);
        Assert.Equal("unusable", scenario.Latest().Outcome);
        Assert.Equal(new[] { CoreSelection }.Concat(GuardSelections), scenario.Latest().Selections);
    }

    [Fact]
    public void TestChangesAppendExtraGuardAndSharedTableClassToStartedReceipt()
    {
        using var scenario = new Scenario();
        scenario.Declare("ExtraNewSourceGuardTests");
        scenario.Declare("CliServedFormsSqliteStoreEffectsTests");
        var roles = new List<AgentRole>();
        var launches = 0;
        var coordinator = new ConductorParallelAcceptanceAttemptCoordinator(
            Path.Combine(scenario.Root, "background-attempts"), Mcg.AgentOrchestrator.Infrastructure.TrunkBranchName.Default,
            isProcessAlive: id => id == 7103,
            launchOwnedProcess: _ =>
            {
                launches++;
                return new ConductorParallelAcceptanceOwnedProcessLaunchResult(7103);
            },
            acquireStableSlotLease: (_, _) => null);
        var driver = scenario.Driver(
            _ => throw new Xunit.Sdk.XunitException("Background evidence must not execute inline."),
            roles.Add, coordinator: coordinator);

        driver.AdvanceOnce(scenario.Goal, ConductorAutonomyPolicy.Conservative);

        var receipt = scenario.Latest();
        Assert.Equal("started", receipt.Outcome);
        Assert.Equal(new[] { CoreSelection }.Concat(GuardSelections).Concat(
            ["Infrastructure.Tests:ExtraNewSourceGuardTests", "Infrastructure.Tests:CliServedFormsSqliteStoreEffectsTests"]),
            receipt.Selections);
        Assert.All(GuardNames.Where(name => name.EndsWith("GuardTests", StringComparison.Ordinal)),
            name => Assert.Single(receipt.Selections, selection => selection == "Infrastructure.Tests:" + name));
        Assert.Empty(receipt.NotRun);
        Assert.Equal(1, launches);
        Assert.Empty(roles);
    }

    [Fact]
    public void SourceChangesDoNotAppendExtraGuardOrSharedTableClass()
    {
        using var scenario = new Scenario();
        scenario.Declare("ExtraNewSourceGuardTests");
        scenario.Declare("CliServedFormsSqliteStoreEffectsTests");
        var requests = new List<string>();
        var driver = scenario.Driver(request =>
        {
            requests.Add(request);
            return scenario.Green(request);
        }, _ => { }, changedPaths: ["src/Mcg.AgentOrchestrator.App/Orchestration/X.cs"]);

        driver.AdvanceOnce(scenario.Goal, ConductorAutonomyPolicy.Conservative);

        var expected = new[] { CoreSelection }.Concat(GuardSelections).ToArray();
        Assert.Equal(expected, Selections(Assert.Single(requests)));
        Assert.Equal(expected, scenario.Latest().Selections);
        Assert.Equal("green", scenario.Latest().Outcome);
    }

    [Fact]
    public void ExtraGuardFailureRetriesDeveloperWithCandidateFailureDetail()
    {
        using var scenario = new Scenario();
        scenario.Declare("ExtraNewSourceGuardTests");
        scenario.Declare("CliServedFormsSqliteStoreEffectsTests");
        const string identity = "ExtraNewSourceGuardTests.FailsOnCandidate";
        var trxPath = Path.Combine(scenario.Root, "extra-guard-failure.trx");
        ConductorDriverTestsActionableRedFailureDetail.WriteFailureTrx(trxPath, identity);
        var roles = new List<AgentRole>();
        var feedback = new List<string>();
        var requests = new List<string>();
        var driver = scenario.Driver(request =>
        {
            requests.Add(request);
            return CandidateOnlyRed(request, [identity], trxPath);
        }, roles.Add, feedback.Add);

        driver.AdvanceOnce(scenario.Goal, ConductorAutonomyPolicy.Conservative);

        Assert.Contains("Infrastructure.Tests:ExtraNewSourceGuardTests", Selections(Assert.Single(requests)));
        Assert.Equal("actionable-red", scenario.Latest().Outcome);
        Assert.Equal([identity], scenario.Latest().FailingTests);
        Assert.Equal([AgentRole.Developer], roles);
        var message = Assert.Single(feedback);
        Assert.StartsWith("ACTIONABLE_CANDIDATE_RED", message, StringComparison.Ordinal);
        Assert.Contains(identity, message, StringComparison.Ordinal);
        Assert.Contains("fatal: synthetic too big", message, StringComparison.Ordinal);
    }

    [Fact]
    public void LegacyStartedReceiptExcludesNewGuardAndSharedTableSelections()
    {
        using var scenario = new Scenario();
        scenario.Declare("ExtraNewSourceGuardTests");
        scenario.Declare("CliServedFormsSqliteStoreEffectsTests");
        scenario.RecordStarted([CoreSelection]);
        var requests = new List<string>();
        var roles = new List<AgentRole>();
        var driver = scenario.Driver(request =>
        {
            requests.Add(request);
            return scenario.Green(request);
        }, roles.Add);

        var exception = Record.Exception(() => driver.AdvanceOnce(scenario.Goal, ConductorAutonomyPolicy.Conservative));

        Assert.Null(exception);
        Assert.Equal([CoreSelection], Selections(Assert.Single(requests)));
        var receipt = scenario.Latest();
        Assert.Equal("green", receipt.Outcome);
        Assert.Equal([CoreSelection], receipt.Selections);
        Assert.Empty(receipt.NotRun);
        Assert.Equal([AgentRole.Tester], roles);
    }

    [Fact]
    public void ChangedMainRevisionWithDriftContinuesUnderRecordedReceipt()
    {
        using var scenario = new Scenario();
        var launches = 0;
        var initial = scenario.Driver(_ => throw new Xunit.Sdk.XunitException("Unexpected inline run."),
            _ => { }, coordinator: scenario.Background(() => launches++));
        initial.AdvanceOnce(scenario.Goal, ConductorAutonomyPolicy.Conservative);
        var recorded = scenario.Latest();
        Assert.Equal("started", recorded.Outcome);
        Assert.Equal(1, launches);
        var removed = GuardNames[0];
        Assert.Contains("Infrastructure.Tests:" + removed, recorded.Selections);
        File.Delete(Path.Combine(scenario.Worktree, "tests", "Mcg.AgentOrchestrator.Infrastructure.Tests", removed + ".cs"));

        const string changedMain = "ccc3333";
        var settings = new AcceptanceGateEngineSettings();
        var guards = PreTesterGuardSelections.Create(new(), scenario.Worktree, [ChangedCorePath],
            changedMain, settings: settings, priorSelections: recorded.Selections);
        var recomputed = guards.Augment(new([new("Core.Tests", "DeclaredCoreTests")], []),
            settings, recorded, ConductorDriver.FormatFindingEvidenceSelection);
        Assert.DoesNotContain("Infrastructure.Tests:" + removed,
            recomputed.Selections.Select(ConductorDriver.FormatFindingEvidenceSelection));

        var requests = new List<string>();
        var inline = new ConductorParallelAcceptanceAttemptCoordinator(
            Path.Combine(scenario.Root, "inline-attempts"), TrunkBranchName.Default,
            runInline: true, acquireStableSlotLease: (_, _) => null);
        var resumed = scenario.Driver(request =>
        {
            requests.Add(request);
            return scenario.Green(request);
        }, _ => { }, coordinator: inline);
        var mainReads = 0;
        resumed.PreTesterMainRevision = _ => { mainReads++; return changedMain; };

        var exception = Record.Exception(() => resumed.AdvanceOnce(scenario.Goal, ConductorAutonomyPolicy.Conservative));

        Assert.Null(exception);
        Assert.Equal(1, mainReads);
        Assert.Equal(recorded.Selections, Selections(Assert.Single(requests)));
        Assert.Equal("green", scenario.Latest().Outcome);
        Assert.Equal(recorded.ReceiptId, scenario.Latest().ReceiptId);
        Assert.Equal(recorded.Selections, scenario.Latest().Selections);
        Assert.Equal(recorded.NotRun, scenario.Latest().NotRun);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DriftNoticeAppearsOnceAcrossTicksAndDriverInstances(bool equalFirst)
    {
        using var scenario = new Scenario();
        var receipt = "drift-" + Guid.NewGuid().ToString("N");
        var launches = 0;
        var coordinator = scenario.Background(() => launches++);
        var driver = scenario.Driver(_ => throw new Xunit.Sdk.XunitException("Unexpected inline run."),
            _ => { }, coordinator: coordinator);
        var prefix = "PRE_TESTER_STARTED_SELECTION_DRIFT receipt=" + receipt + " ";
        var output = AsyncLocalConsoleRouter.Capture(() =>
        {
            if (equalFirst)
            {
                scenario.RecordStarted([CoreSelection], receiptId: receipt);
                driver.AdvanceOnce(scenario.Goal, ConductorAutonomyPolicy.Conservative);
            }
            scenario.RecordStarted([CoreSelection, "Core.Tests:RemovedCoreTests"], receiptId: receipt);
            for (var tick = 0; tick < 3; tick++)
            {
                if (tick == 1)
                    driver = scenario.Driver(_ => throw new Xunit.Sdk.XunitException("Unexpected inline run."),
                        _ => { }, coordinator: coordinator);
                var result = driver.AdvanceOnce(scenario.Goal, ConductorAutonomyPolicy.Conservative);
                Assert.IsType<ConductorAdvanceOutcome.Held>(result.Outcome);
                Assert.Equal("started", scenario.Latest().Outcome);
                Assert.Equal(receipt, scenario.Latest().ReceiptId);
            }
        });

        var notice = Assert.Single(output.Split(Environment.NewLine)
            .Where(line => line.StartsWith(prefix, StringComparison.Ordinal)));
        Assert.Contains("missing=Core.Tests:RemovedCoreTests added=<none>", notice, StringComparison.Ordinal);
        Assert.True(launches > 0, "Background evidence must have been launched.");
    }

    [Theory]
    [InlineData("base-equals-candidate")]
    [InlineData("tests-status-not-deferred")]
    [InlineData("no-resolved-classes")]
    [InlineData("no-normalized-classes")]
    public void LostDeclarationOrClassesAbandonsStartedEntryAndContinues(string reason)
    {
        var tests = reason switch
        {
            "tests-status-not-deferred" => "not-run - DeclaredCoreTests",
            "no-resolved-classes" => "deferred - MissingClass",
            "no-normalized-classes" => "deferred - UnconfiguredClass",
            _ => "deferred - DeclaredCoreTests"
        };
        using var scenario = new Scenario(tests: tests,
            baseCommit: reason == "base-equals-candidate" ? CandidateSha : BaseSha);
        if (reason == "no-normalized-classes") scenario.Declare("UnconfiguredClass", "Unconfigured.Tests");
        scenario.RecordStarted(["Core.Tests:RemovedCoreTests", CoreSelection, CoreSelection],
            ["SecondMissingTests", "FirstMissingTests", "SecondMissingTests"]);
        var prior = scenario.Latest();
        var runs = 0;
        var roles = new List<AgentRole>();
        var driver = scenario.Driver(_ =>
        {
            runs++;
            throw new Xunit.Sdk.XunitException("Abandoned evidence must not run.");
        }, roles.Add);

        var exception = Record.Exception(() => driver.AdvanceOnce(scenario.Goal, ConductorAutonomyPolicy.Conservative));

        Assert.Null(exception);
        Assert.Equal(0, runs);
        Assert.Equal([AgentRole.Tester], roles);
        var abandoned = scenario.Latest();
        Assert.Equal("abandoned", abandoned.Outcome);
        Assert.Equal(prior.CandidateSha, abandoned.CandidateSha);
        Assert.Equal(prior.ReceiptId, abandoned.ReceiptId);
        Assert.Equal(prior.Selections, abandoned.Selections);
        Assert.Equal(prior.NotRun, abandoned.NotRun);
        Assert.Null(abandoned.ResultPath);
        Assert.Empty(abandoned.FailingTests);
        string[] AbandonLines() => scenario.Goal.Timeline
            .Where(evt => evt.TaskId == scenario.Tester.Id && evt.Kind == ProgressKind.FindingEvidenceRunRecorded &&
                PreTesterEvidenceIndexLines.Parse(evt.Message)?.Outcome == "abandoned")
            .Select(evt => evt.Message).ToArray();
        Assert.EndsWith("; abandoned_reason=" + reason, Assert.Single(AbandonLines()), StringComparison.Ordinal);

        driver.AdvanceOnce(scenario.Goal, ConductorAutonomyPolicy.Conservative);

        Assert.Equal("abandoned", scenario.Latest().Outcome);
        Assert.Single(AbandonLines());
        Assert.Equal(0, runs);
    }

    private static string[] Selections(string request) =>
        request.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static FocusedEvidenceRunResult CandidateOnlyRed(
        string request, IReadOnlyList<string> failures, string? trxPath = null)
    {
        var red = CandidateRedFindingEvidence(request, CandidateSha, failures[0]);
        var candidate = red.Arms!.Single(arm => arm.Arm == FindingEvidenceArm.Candidate);
        var check = candidate.Checks.Single() with
        {
            FailingTestIdentities = failures,
            TestResultPaths = trxPath is null ? [] : [trxPath]
        };
        return red with { Checks = [check], Arms = [candidate with { Checks = [check] }] };
    }

    private sealed class Scenario : IDisposable
    {
        public string Root { get; } = ConductorDriverTests.CreateTempDirectory();
        public AgentOrchestratorKernel Kernel { get; }
        public Goal Goal { get; }
        public TaskSpec Developer { get; }
        public TaskSpec Tester { get; }
        public string Worktree => GoalWorktrees.WorktreePath(Root, Goal.Id);

        public Scenario(string? missingGuard = null, string tests = "deferred - DeclaredCoreTests", string baseCommit = BaseSha)
        {
            (Kernel, Goal) = SoftwareGoal("Pre-Tester focused source evidence");
            Developer = Goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
            Tester = Goal.Tasks.Single(task => task.RequiredRole == AgentRole.Tester);
            foreach (var task in Goal.Tasks.TakeWhile(task => task.Id != Developer.Id))
                PassVerification(Kernel, Goal, task);
            DispatchTask(Kernel, Goal, Developer, baseCommit: baseCommit);
            var output = string.Join(Environment.NewLine,
                "WORKER_RESULT:", "files: " + ChangedCorePath, "commands: none",
                "tests: " + tests, "commit: none", "blockers: none",
                "assigned_scope_complete: true", "model_fit: test/model - adequate - fixture",
                "skills: none", "confidence: high", "END_WORKER_RESULT");
            Kernel.RecordTaskVerification(Goal.Id, Developer.Id, new TaskVerificationRecord(
                "test.exe", "C:\\tmp", 0, output, "", DateTimeOffset.UtcNow,
                WorkerResultPresent: true, HasCommittedChanges: true));
            Directory.CreateDirectory(Worktree);
            File.WriteAllText(Path.Combine(Worktree, ".git"), "gitdir: fixture");
            Declare("DeclaredCoreTests", "Mcg.AgentOrchestrator.Core.Tests");
            foreach (var name in GuardNames.Where(name => name != missingGuard)) Declare(name);
        }

        public void Declare(string name, string projectName = "Mcg.AgentOrchestrator.Infrastructure.Tests")
        {
            var project = Path.Combine(Worktree, "tests", projectName);
            Directory.CreateDirectory(project);
            File.WriteAllText(Path.Combine(project, projectName + ".csproj"), "<Project />");
            File.WriteAllText(Path.Combine(project, name + ".cs"), "public class " + name + " { [Xunit.Fact] public void Runs() {} }");
        }

        public PreTesterEvidenceEntry Latest() =>
            Assert.IsType<PreTesterEvidenceEntry>(PreTesterEvidenceIndexLines.Latest(Goal, Tester.Id, CandidateSha));

        public void RecordStarted(IReadOnlyList<string> selections, IReadOnlyList<string>? notRun = null,
            string receiptId = "pre-change-receipt") =>
            Kernel.RecordFindingEvidenceRequest(Goal.Id, Tester.Id,
                PreTesterEvidenceIndexLines.FormatMarker(new PreTesterEvidenceEntry(
                    "started", CandidateSha, receiptId, selections, notRun ?? [], null, [])));

        public ConductorParallelAcceptanceAttemptCoordinator Background(Action launched) => new(
            Path.Combine(Root, "background-attempts"), TrunkBranchName.Default,
            isProcessAlive: id => id == 7103,
            launchOwnedProcess: _ =>
            {
                launched();
                return new ConductorParallelAcceptanceOwnedProcessLaunchResult(7103);
            },
            acquireStableSlotLease: (_, _) => null);

        public FocusedEvidenceRunResult Green(string request) =>
            ConductorDriverTestsFindingEvidenceReuse.RetainedEvidenceWithExecutedClasses(Root, request, CandidateSha);

        public ConductorDriver Driver(
            Func<string, FocusedEvidenceRunResult> run,
            Action<AgentRole> dispatched,
            Action<string>? feedback = null,
            ConductorParallelAcceptanceAttemptCoordinator? coordinator = null,
            IReadOnlyList<string>? changedPaths = null)
        {
            var driver = MakeDriver(
                getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
                getPreReviewEvidenceContext: _ => NoPreReviewContext(CandidateSha),
                getLandingFileScopes: _ => changedPaths ?? [ChangedCorePath],
                focusedEvidenceAttemptCoordinator: coordinator ?? new ConductorParallelAcceptanceAttemptCoordinator(
                    Path.Combine(Root, "attempts"), Mcg.AgentOrchestrator.Infrastructure.TrunkBranchName.Default, runInline: true, acquireStableSlotLease: (_, _) => null),
                runFocusedEvidence: (_, request) => run(request),
                dispatchAndStart: goal =>
                {
                    dispatched(goal.Tasks.First(task => task.Status == WorkTaskStatus.Assigned).RequiredRole);
                    return DispatchStartOutcome.Started();
                },
                retryTaskWithCause: (goalId, taskId, message, round, cause) =>
                {
                    feedback?.Invoke(message);
                    return Kernel.RetryTask(goalId, taskId, message, retryRoundKind: round, retryCause: cause);
                },
                recordFindingEvidenceRequest: (goalId, taskId, message) =>
                    Kernel.RecordFindingEvidenceRequest(goalId, taskId, message),
                recordFindingEvidenceRun: (goalId, taskId, message) =>
                    Kernel.RecordFindingEvidenceRun(goalId, taskId, message),
                executionDirectory: Root);
            driver.OverrideDeveloperCompletionStructuralPreflightForTests(
                _ => new DeveloperCompletionStructuralFindings(false, "clean"), TimeSpan.FromSeconds(5));
            return driver;
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
