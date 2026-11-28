using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using AcceptanceManifestCheck = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.AcceptanceManifestCheck;
using PlanningAttempt = Mcg.AgentOrchestrator.App.Orchestration.PreTesterTestImpactPlanAttemptCoordinator;

[Trait("Isolation", "ProcessSpawning")]
public sealed class TickTestImpactPlanMemoTests
{
    private const string Source = "src/Mcg.AgentOrchestrator.Infrastructure/Widget.cs";
    private const string Generated = "src/Widget/obj/Generated.cs";
    private const string IgnoredGenerated = "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/obj/Debug/IgnoredGenerated.cs";
    private const string IgnoredSource = "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/IgnoredWidgetInput.cs";
    private const string GuardSource = "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/WidgetGuardTests.cs";

    [Fact]
    public void FourDispatchStartConsumersShareOnePlannerCall()
    {
        using var fixture = new Fixture();
        fixture.WriteIgnoredSource(IgnoredGenerated);
        var goal = fixture.CreateGoal(out var kernel);
        var driver = fixture.CreateDriver(kernel);
        var reviewer = Assert.Single(goal.Tasks);

        // Exercise each production entry with a cold memo so a direct-planner
        // bypass cannot hide behind a different consumer's cache miss.
        Action[] consumers =
        [
            () =>
            {
                ReadGuards(driver, fixture);
                Assert.Equal(fixture.Candidate.Head, driver.GetPreReviewEvidenceContext(goal).CandidateSha);
            },
            () =>
            {
                FindingEvidenceContinuationCoordinator.TrySelectFindingEvidenceContinuation(driver, goal, reviewer, out _, out var reason);
                Assert.NotEqual("candidate-unavailable", reason);
            },
            () => Assert.NotNull(driver._resolveCandidateIdentity(goal).Identity),
            () => Assert.True(driver.GetPreReviewEvidenceContext(goal).MappingNeedsInput)
        ];
        foreach (var consumer in consumers)
        {
            using var coldTick = driver.BeginTestImpactPlanTick();
            driver.BeginTick();
            var before = fixture.PlannerCalls;
            consumer();
            Assert.Equal(before + 1, fixture.PlannerCalls);
        }

        using var warmTick = driver.BeginTestImpactPlanTick();
        driver.BeginTick();
        var plannerCalls = fixture.PlannerCalls;
        var guardCalls = fixture.GuardCalls;
        foreach (var consumer in consumers)
            consumer();
        ReadGuards(driver, fixture);
        ReadDecision(driver, goal);

        Assert.Equal(plannerCalls + 1, fixture.PlannerCalls);
        Assert.Equal(guardCalls + 1, fixture.GuardCalls);
    }

    [Theory]
    [InlineData(IgnoredSource, false)]
    [InlineData(IgnoredGenerated, true)]
    public void IgnoredPlanningInputsDoNotReuseMemo(string ignoredPath, bool explicitlyChanged)
    {
        using var fixture = new Fixture();
        fixture.WriteIgnoredSource(ignoredPath);
        var candidate = fixture.Candidate;
        var memo = fixture.CreateMemo();
        string[] paths = explicitlyChanged ? [Source, ignoredPath] : [Source];
        var summary = RepositoryChangeClassifier.Classify(paths);

        memo.Plan(candidate, summary);
        memo.Plan(candidate, summary);
        memo.SelectReverseDependents(candidate, paths);
        memo.SelectReverseDependents(candidate, paths);

        Assert.Equal(2, fixture.PlannerCalls);
        Assert.Equal(2, fixture.GuardCalls);
    }

    [Fact]
    public void PreReviewRecordedIdentitySharesCandidateManifestPlan()
    {
        using var fixture = new Fixture();
        var goal = fixture.CreateGoal(out var kernel);
        var driver = fixture.CreateDriver(kernel);
        var candidateSha = fixture.Candidate.Head;
        var manifest = GoalAcceptanceVerifier.ComputeEffectiveAcceptancePlanIdentity(fixture.Worktree, fixture.ChangedFiles);
        var direct = PreReviewEvidenceReceipts.ResolveRecordedCandidateIdentity(
            fixture.Root, goal.Id, candidateSha, "main", projectHomeDirectory: null, executedManifestIdentity: manifest);
        Assert.False(string.IsNullOrWhiteSpace(direct.TreeSha));
        Assert.Equal(manifest, direct.ManifestIdentity);

        var memo = fixture.CreateMemo();
        driver.TestImpactPlanMemoFactory = () => memo;
        (string? TreeSha, string? ManifestIdentity) ReadRecordedIdentity() =>
            TickTestImpactPlanMemo.ResolvePreReviewRecordedCandidateIdentity(
                memo, fixture.Root, goal.Id, candidateSha, "main", projectHomeDirectory: null, executedManifestIdentity: manifest);

        using var tick = driver.BeginTestImpactPlanTick();
        driver.BeginTick();
        Assert.Equal(direct, ReadRecordedIdentity());
        Assert.Equal(1, fixture.PlannerCalls);
        Assert.NotNull(driver._resolveCandidateIdentity(goal).Identity);
        Assert.Equal(direct, ReadRecordedIdentity());
        Assert.Equal(1, fixture.PlannerCalls);
    }

    [Fact]
    public void DistinctCandidateHeadsPlanSeparately()
    {
        using var fixture = new Fixture();
        var memo = fixture.CreateMemo();
        var candidate = fixture.Candidate;
        var summary = RepositoryChangeClassifier.Classify([Source]);
        memo.Plan(candidate, summary);
        Git(fixture.Worktree, "commit", "--allow-empty", "-m", "Advance candidate");
        var next = fixture.Candidate;
        Assert.NotEqual(candidate.Head, next.Head);
        Assert.Equal(candidate.MainRevision, next.MainRevision);

        memo.Plan(next, summary);
        memo.Plan(next, summary);

        Assert.Equal(2, fixture.PlannerCalls);
    }

    [Fact]
    public void DistinctMainRevisionsPlanSeparately()
    {
        using var fixture = new Fixture();
        var memo = fixture.CreateMemo();
        var candidate = fixture.Candidate;
        var summary = RepositoryChangeClassifier.Classify([Source]);
        memo.Plan(candidate, summary);
        Git(fixture.Root, "commit", "--allow-empty", "-m", "Advance main");
        var next = fixture.Candidate;
        Assert.NotEqual(candidate.MainRevision, next.MainRevision);
        Assert.Equal(candidate.Head, next.Head);
        Assert.Equal(candidate.MergeBase, next.MergeBase);

        memo.Plan(next, summary);
        memo.Plan(next, summary);

        Assert.Equal(2, fixture.PlannerCalls);
    }

    [Fact]
    public void DistinctChangedFileSetsPlanSeparately()
    {
        using var fixture = new Fixture();
        var memo = fixture.CreateMemo();
        var candidate = fixture.Candidate;
        memo.Plan(candidate, RepositoryChangeClassifier.Classify([Source]));
        var withGenerated = memo.Plan(candidate, RepositoryChangeClassifier.Classify([Generated, Source]));
        var reordered = memo.Plan(candidate, RepositoryChangeClassifier.Classify([Source, Generated]));

        Assert.Same(withGenerated, reordered);
        Assert.Equal(2, fixture.PlannerCalls);
    }

    [Fact]
    public void NextTickDoesNotReuseMemo()
    {
        using var fixture = new Fixture();
        var goal = fixture.CreateGoal(out var kernel);
        var driver = fixture.CreateDriver(kernel);

        using (driver.BeginTestImpactPlanTick())
        {
            driver.BeginTick(kernel, 1);
            driver.GetPreReviewEvidenceContext(goal);
            driver.BeginTick(kernel, 1);
            driver.GetPreReviewEvidenceContext(goal);
            Assert.Equal(1, fixture.PlannerCalls);
        }

        using (driver.BeginTestImpactPlanTick())
        {
            driver.BeginTick(kernel, 2);
            driver.GetPreReviewEvidenceContext(goal);
            Assert.Equal(2, fixture.PlannerCalls);
        }

        Git(fixture.Worktree, "commit", "--allow-empty", "-m", "Advance candidate outside the tick");
        Assert.Equal(fixture.Candidate.Head, driver.GetPreReviewEvidenceContext(goal).CandidateSha);
        Assert.Equal(2, fixture.PlannerCalls);
    }

    [Fact]
    public void PlannerFailureIsNotCached()
    {
        using var fixture = new Fixture();
        var calls = 0;
        var memo = new TickTestImpactPlanMemo((summary, path) =>
        {
            if (++calls == 1)
                throw new InvalidOperationException("Injected planning failure");
            return RepositoryTestImpactPlanner.Plan(summary, path);
        });
        var candidate = fixture.Candidate;
        var change = RepositoryChangeClassifier.Classify([Source]);

        Assert.Throws<InvalidOperationException>(() => memo.Plan(candidate, change));
        var recovered = memo.Plan(candidate, change);
        Assert.Same(recovered, memo.Plan(candidate, change));
        Assert.Equal(2, calls);
    }

    [Fact]
    public void DistinctManifestChecksScopeSeparately()
    {
        using var fixture = new Fixture();
        var calls = 0;
        var memo = new TickTestImpactPlanMemo((_, _) =>
        {
            calls++;
            return new(true, false, "Focused impact",
                [new("widget tests", ["dotnet", "test", "--project", "tests/Widget.Tests/Widget.Tests.csproj"], "Changed widget")]);
        });
        var candidate = fixture.Candidate;
        var summary = RepositoryChangeClassifier.Classify([Source]);
        var declared = new AcceptanceManifestCheck { Type = "dotnet-test", Project = "tests/Widget.Tests/Widget.Tests.csproj" };
        var other = new AcceptanceManifestCheck { Type = "dotnet-test", Project = "tests/Other.Tests/Other.Tests.csproj" };

        ManifestScopedImpactPlan.WithRawPlanner((changes, _) => memo.Plan(candidate, changes), () =>
        {
            Assert.Single(ManifestScopedImpactPlan.Plan(summary, fixture.Worktree, [declared]).Checks);
            Assert.Empty(ManifestScopedImpactPlan.Plan(summary, fixture.Worktree, [other]).Checks);
            Assert.Single(ManifestScopedImpactPlan.Plan(summary, fixture.Worktree, [declared]).Checks);
            return true;
        });
        Assert.Equal(1, calls);
    }

    [Fact]
    public void DistinctTargetHomesScopeSeparately()
    {
        using var fixture = new Fixture();
        var memo = fixture.CreateMemo();
        var candidate = fixture.Candidate;
        var summary = RepositoryChangeClassifier.Classify([Source]);
        var home = new WorkerTargetHome();
        var foreign = new WorkerTargetHome(false, _ => Path.Combine(fixture.Worktree, "missing-target-manifest.json"));
        RepositoryTestImpactPlan Plan(WorkerTargetHome target) =>
            GoalTargetImpactPlanScope.Plan(summary, fixture.Worktree, target, (changes, _) => memo.Plan(candidate, changes));

        var homePlan = Plan(home);
        Assert.NotEmpty(homePlan.Checks);
        var foreignPlan = Plan(foreign);
        Assert.Empty(foreignPlan.Checks);
        Assert.Contains("missing-target-manifest.json", foreignPlan.Summary, StringComparison.Ordinal);
        Assert.Equal(JsonSerializer.Serialize(homePlan), JsonSerializer.Serialize(Plan(home)));
        Assert.Equal(1, fixture.PlannerCalls);
    }

    [Fact]
    public void MemoBackedOutputsEqualDirectPlanning()
    {
        using var fixture = new Fixture();
        var directGoal = fixture.CreateGoal(out var directKernel);
        var memoGoal = fixture.CreateGoal(out var memoKernel);
        var directDriver = fixture.CreateDriver(directKernel);
        var memoDriver = fixture.CreateDriver(memoKernel);
        var direct = ReadOutputs(directDriver, directGoal, fixture);
        Assert.Equal(0, fixture.PlannerCalls);
        Assert.Equal(0, fixture.GuardCalls);

        using var tick = memoDriver.BeginTestImpactPlanTick();
        memoDriver.BeginTick();
        var memoBacked = ReadOutputs(memoDriver, memoGoal, fixture);

        Assert.Equal(direct, memoBacked);
        Assert.Equal(1, fixture.PlannerCalls);
        Assert.Equal(1, fixture.GuardCalls);
    }

    [Theory]
    [InlineData(false, "failed: planning worker failed")]
    [InlineData(true, "owner-dead: Planning process 42 exited without a result.")]
    public void BackgroundGuardRequestHoldsWithoutPlanningOrDispatch(bool ownerDied, string failure)
    {
        using var fixture = new Fixture();
        var candidate = fixture.Candidate;
        var goal = fixture.CreateDeferredGoal(candidate.MergeBase);
        var launch = new PlanningLaunch(fixture.Root);
        var dispatches = 0;
        var evidenceRuns = 0;
        var driver = ConductorDriverTests.MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            getPreReviewEvidenceContext: _ => ConductorDriverTests.NoPreReviewContext(candidate.Head),
            getLandingFileScopes: _ => fixture.ChangedFiles,
            getFindingEvidenceEngineSettings: _ => AcceptanceGateEngineSettings.Load(fixture.Worktree),
            runFocusedEvidence: (_, _) =>
            {
                evidenceRuns++;
                throw new InvalidOperationException("Focused evidence ran before planning was ready.");
            },
            dispatchAndStart: _ =>
            {
                dispatches++;
                return DispatchStartOutcome.Started();
            },
            executionDirectory: fixture.Root);
        driver.PreTesterMainRevision = _ => candidate.MergeBase;
        driver.PreTesterPlanningAttempts = launch.Coordinator;
        driver.TestImpactPlanMemoFactory = fixture.CreateMemo;
        driver.OverrideDeveloperCompletionStructuralPreflightForTests(
            _ => new DeveloperCompletionStructuralFindings(false, "clean"), TimeSpan.FromSeconds(5));

        Assert.StartsWith("PRE_TESTER_PLANNING_PENDING:", Advance().Reason, StringComparison.Ordinal);
        var (path, id) = Assert.Single(launch.Attempts);
        if (ownerDied)
            launch.OwnerStatus = SpawnTrackedProcessStatus.DeadOrRecycled;
        else
            Assert.Equal(1, PlanningAttempt.RunAttempt(path, id, _ => throw new InvalidOperationException("planning worker failed")));

        var failed = Advance();
        Assert.StartsWith("PRE_TESTER_PLANNING_FAILED:", failed.Reason, StringComparison.Ordinal);
        Assert.Contains(failure, failed.Reason, StringComparison.Ordinal);
        Assert.Equal(failed.Reason, Advance().Reason);
        Assert.Single(launch.Attempts);

        ConductorAdvanceOutcome.Held Advance()
        {
            using var tick = driver.BeginTestImpactPlanTick();
            var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);
            Assert.Equal(0, fixture.PlannerCalls);
            Assert.Equal(0, fixture.GuardCalls);
            Assert.Equal(0, dispatches);
            Assert.Equal(0, evidenceRuns);
            return Assert.IsType<ConductorAdvanceOutcome.Held>(result.Outcome);
        }
    }

    [Fact]
    public void BackgroundGuardRequestServesStoredResultEqualToInlinePlanning()
    {
        using var fixture = new Fixture();
        var launch = new PlanningLaunch(fixture.Root);
        var memo = fixture.CreateMemo();
        var candidate = fixture.Candidate;
        var changed = fixture.ChangedFiles;
        var settings = AcceptanceGateEngineSettings.Load(fixture.Worktree);
        var cache = new PreTesterChangedFileGuardClasses();

        Assert.Null(ReadBackground(out var hold));
        Assert.StartsWith("PRE_TESTER_PLANNING_PENDING:", hold, StringComparison.Ordinal);
        Assert.Equal(0, fixture.PlannerCalls);
        Assert.Equal(0, fixture.GuardCalls);
        var (path, id) = Assert.Single(launch.Attempts);
        Assert.Equal(0, PlanningAttempt.RunAttempt(path, id, key => PlanningAttempt.Compute(key)));

        var ready = ReadBackground(out hold);
        Assert.Null(hold);
        Assert.True(ready.HasValue);
        Assert.Equal(0, fixture.PlannerCalls);
        Assert.Equal(0, fixture.GuardCalls);
        Assert.Contains(ready.Value.Guards.Selections, selection => selection.TestClass == "WidgetGuardTests");
        var expected = TickTestImpactPlanMemo.CreateGuardRequest(fixture.CreateMemo(), "main", cache,
            fixture.Worktree, changed, candidate.MergeBase, new([], []), settings, null, selection => selection.TestClass,
            (_, _) => { }, null, null);
        Assert.Equal(JsonSerializer.Serialize(expected.Guards.Selections), JsonSerializer.Serialize(ready.Value.Guards.Selections));
        Assert.Equal(JsonSerializer.Serialize(expected.Selection), JsonSerializer.Serialize(ready.Value.Selection));
        Assert.Equal(1, fixture.PlannerCalls);
        Assert.Equal(1, fixture.GuardCalls);

        // A second lookup in this tick uses the memo even if the attempt artifact disappears.
        File.Delete(Path.Combine(Path.GetDirectoryName(path)!, "result.json"));
        var memoized = ReadBackground(out hold);
        Assert.Null(hold);
        Assert.True(memoized.HasValue);
        Assert.Equal(JsonSerializer.Serialize(expected.Selection), JsonSerializer.Serialize(memoized.Value.Selection));
        Assert.Equal(1, fixture.PlannerCalls);
        Assert.Equal(1, fixture.GuardCalls);
        Assert.Single(launch.Attempts);

        (PreTesterGuardSelections Guards, DeveloperDeferredTestSelection Selection, bool Abandoned)? ReadBackground(out string? reason) =>
            TickTestImpactPlanMemo.CreateGuardRequestOrPending(launch.Coordinator, memo, "main", cache, fixture.Worktree,
                changed, candidate.MergeBase, candidate.Head, new([], []), settings, null, selection => selection.TestClass,
                (_, _) => { }, null, null, out reason);
    }

    private sealed class PlanningLaunch
    {
        internal List<(string Path, Guid Id)> Attempts { get; } = [];
        internal SpawnTrackedProcessStatus OwnerStatus { get; set; } = SpawnTrackedProcessStatus.LiveMatch;
        internal PlanningAttempt Coordinator { get; }

        internal PlanningLaunch(string root) =>
            Coordinator = new(Path.Combine(root, "planning"), root, (path, id) =>
            {
                Attempts.Add((path, id));
                return new(42, DateTimeOffset.UnixEpoch, "planning-child");
            }, _ => OwnerStatus);
    }

    private static string ReadOutputs(ConductorDriver driver, Goal goal, Fixture fixture)
    {
        var guards = ReadGuards(driver, fixture);
        Assert.Contains(guards.Guards.Selections, selection => selection.TestClass == "WidgetGuardTests");
        var identity = driver._resolveCandidateIdentity(goal);
        Assert.True(identity.Identity is not null, identity.Failure);
        var context = driver.GetPreReviewEvidenceContext(goal);
        var decision = ReadDecision(driver, goal);
        var receipt = Assert.IsType<PreReviewEvidenceReceipt>(Assert.Single(goal.Tasks).PreReviewEvidenceReceipt);
        return JsonSerializer.Serialize(new
        {
            Guards = guards.Guards.Selections,
            guards.Selection,
            identity.Identity,
            Context = context,
            Decision = decision,
            Receipt = receipt with { RecordedAt = DateTimeOffset.UnixEpoch }
        });
    }

    private static (PreTesterGuardSelections Guards, DeveloperDeferredTestSelection Selection, bool Abandoned)
        ReadGuards(ConductorDriver driver, Fixture fixture) =>
        TickTestImpactPlanMemo.CreateGuardRequestOrPending(null, driver.CurrentTestImpactPlanMemo, "main",
            new(), fixture.Worktree, fixture.ChangedFiles, fixture.Candidate.MergeBase, fixture.Candidate.Head,
            new([], []), AcceptanceGateEngineSettings.Load(fixture.Worktree), null, ConductorDriver.FormatFindingEvidenceSelection,
            (_, _) => { }, null, null, out _)!.Value;

    private static ConductorAdvanceOutcome.Escalated ReadDecision(ConductorDriver driver, Goal goal)
    {
        Assert.True(driver.TryRunPreReviewEvidenceStage(goal, goal.Id.Value[..8], ConductorAutonomyPolicy.Permissive,
            GoalLifecycleState.WorkspaceReady, out var result));
        var outcome = Assert.IsType<ConductorAdvanceOutcome.Escalated>(result.Outcome);
        Assert.Contains("PRE_REVIEW_MAPPING_NEEDS_INPUT", outcome.Reason, StringComparison.Ordinal);
        return outcome;
    }

    private static void Git(string root, params string[] args)
    {
        var result = GitCli.Run(root, args);
        Assert.True(result.ProcessStarted && !result.DrainTimedOut && result.ExitCode == 0,
            $"git {string.Join(' ', args)}: {result.Error}{result.Output}");
    }

    private sealed class Fixture : IDisposable
    {
        private const string TestProject = "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj";
        private readonly GoalId _goalId = GoalId.New();
        private readonly TaskId _reviewerId = TaskId.New();

        internal string Root { get; } = SharedTestSupport.CreateTempDirectory();
        internal string Worktree { get; }
        internal string[] ChangedFiles => GoalAcceptanceEvidenceBundleBuilder.GetChangedFiles(Worktree, "main");
        internal int PlannerCalls { get; private set; }
        internal int GuardCalls { get; private set; }
        internal TickTestImpactPlanMemo.Candidate Candidate =>
            Assert.IsType<TickTestImpactPlanMemo.Candidate>(TickTestImpactPlanMemo.Candidate.Read(Worktree, "main"));

        internal Fixture()
        {
            MainBranchGitRepositoryTemplate.CopyTo(Root, includeSkillCatalog: false);
            Write(Root, ".gitignore", ".orchestrator/\n**/obj/\nIgnoredWidgetInput.cs\n");
            Write(Root, Source, "public sealed class Widget { }");
            Write(Root, TestProject, "<Project Sdk=\"Microsoft.NET.Sdk\" />");
            Write(Root, "config/acceptance-manifest.json",
                $$"""{"checks":[{"name":"infrastructure tests","type":"dotnet-test","project":"{{TestProject}}"}]}""");
            Git(Root, "add", ".");
            Git(Root, "commit", "-m", "Add test impact inputs");
            Worktree = GoalWorktrees.Ensure(Root, _goalId, trunkBranch: "main");
            Write(Worktree, Source, "public sealed class Widget { public int Value => 1; }");
            Write(Worktree, Generated, "public sealed class Generated { }");
            Write(Worktree, GuardSource, "using Xunit; public sealed class WidgetGuardTests { [Fact] public void ChecksWidget() { } }");
            Git(Worktree, "add", "--force", Source, Generated, GuardSource);
            Git(Worktree, "commit", "-m", "Change candidate");
        }

        internal void WriteIgnoredSource(string path)
        {
            Write(Worktree, path, "public sealed class IgnoredWidgetInput { }");
            Git(Worktree, "check-ignore", "--", path);
        }

        internal Goal CreateGoal(out AgentOrchestratorKernel kernel)
        {
            kernel = new();
            var reviewer = new TaskSpec(_reviewerId, "Review the candidate", AgentRole.Reviewer);
            reviewer.SetStatus(WorkTaskStatus.Assigned);
            return kernel.CreateGoal(_goalId, "Verify test impact reuse", [reviewer]);
        }

        internal Goal CreateDeferredGoal(string baseCommit)
        {
            var kernel = new AgentOrchestratorKernel();
            var developer = new TaskSpec(TaskId.New(), "Implement the candidate", AgentRole.Developer);
            var tester = new TaskSpec(TaskId.New(), "Test the candidate", AgentRole.Tester);
            var goal = kernel.CreateGoal(_goalId, "Verify pre-Tester planning", [developer, tester]);
            kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
            ConductorDriverTests.DispatchTask(kernel, goal, developer, baseCommit: baseCommit);
            var report = """
                WORKER_RESULT:
                files: tests/Mcg.AgentOrchestrator.Infrastructure.Tests/WidgetGuardTests.cs
                commands: none
                tests: deferred - WidgetGuardTests
                commit: none
                blockers: none
                assigned_scope_complete: true
                model_fit: test/model - adequate - fixture
                skills: none
                confidence: high
                END_WORKER_RESULT
                """;
            kernel.RecordTaskVerification(goal.Id, developer.Id, new TaskVerificationRecord(
                "test.exe", Worktree, 0, report, "", DateTimeOffset.UnixEpoch,
                WorkerResultPresent: true, HasCommittedChanges: true));
            return goal;
        }

        internal ConductorDriver CreateDriver(AgentOrchestratorKernel kernel)
        {
            var workspace = CreateSetUpWorkspace(Root);
            return new(kernel, workspace, new GoalAcceptanceVerifier(new CrosswalkExecutableResolver()),
                AgentCatalog.Default().Agents, WorkerProfileCatalog.Default(), runAcceptanceAttemptsInCurrentProcess: true)
            {
                TestImpactPlanMemoFactory = CreateMemo
            };
        }

        internal TickTestImpactPlanMemo CreateMemo() => new(
            (summary, root) =>
            {
                PlannerCalls++;
                return RepositoryTestImpactPlanner.Plan(summary, root);
            },
            (root, paths) =>
            {
                GuardCalls++;
                return RepositoryRecheckTestSelection.Read(root, paths);
            });

        public void Dispose() => SharedTestSupport.RemoveTempDirectory(Root);

        private static void Write(string root, string path, string content)
        {
            var fullPath = Path.Combine(root, path);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllText(fullPath, content);
        }
    }
}
