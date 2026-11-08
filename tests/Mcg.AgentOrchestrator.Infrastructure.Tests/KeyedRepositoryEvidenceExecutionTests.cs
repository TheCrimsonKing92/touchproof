using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using static ConductorDriverTests;
using GitFixture = GoalRepositoryWorktreeProvisionerFixture;

// Parallel-safe: repositories, registry, stores and attempt artifacts belong to each fixture.
// Git is real; the acceptance verifier is a recording seam and launches no test processes.
public sealed class KeyedRepositoryEvidenceExecutionTests
{
    [Fact]
    public void Resolve_DeclaredRepository_BindsWorktreeHomeBranchAndHead()
    {
        using var fixture = new Fixture();
        var (target, failure) = KeyedRepositoryEvidenceTarget.TryResolve(fixture.Goal, "Crosswalk", fixture.Registry);
        Assert.NotNull(target);
        Assert.Equal(string.Empty, failure);
        Assert.Equal("crosswalk", target.Key);
        Assert.Equal(fixture.SecondaryWorktree, target.WorktreePath);
        Assert.Equal(fixture.ProjectHome, target.ProjectHomeDirectory);
        Assert.Equal("trunk", target.IntegrationBranch);
        Assert.Equal(fixture.SecondaryHead, target.CandidateHead);
        Assert.NotEqual(fixture.HomeHead, target.CandidateHead);
        Assert.NotNull(target.EngineSettings.DeclaredGoTestCheck);
    }

    [Fact]
    public void Execute_ProductionRunner_PassesTargetAndControlInputsToVerifier()
    {
        using var fixture = new Fixture();
        var verifier = new RecordingVerifier();
        var driver = new ConductorDriver(fixture.Kernel, fixture.Repositories.Workspace, verifier,
            fixture.Repositories.Agents, fixture.Repositories.Profiles,
            cleanupHooks: new GoalWorktreeCleanupHooks { BuildStorageRoot = new(Path.Combine(fixture.Repositories.Home.Root, "builds")) });
        var target = fixture.Resolve();
        var paths = new[] { "src/record.go" };
        var result = driver.RunKeyedFocusedEvidence(fixture.Goal, target, "record:TestReadInvalidFixtures", null,
            false, CancellationToken.None, FindingEvidenceNegativeControl.RevertSrc, paths, null, null);

        Assert.True(result.Accepted);
        var call = Assert.Single(verifier.Calls);
        Assert.Equal(target.WorktreePath, call.Worktree);
        Assert.Equal(target.ProjectHomeDirectory, call.Owner.ProjectHomeDirectory);
        Assert.Equal("trunk", call.Owner.IntegrationBranch);
        Assert.Equal(target.CandidateHead, call.Owner.Identity.CandidateSha);
        Assert.NotNull(call.Owner.Settings.DeclaredGoTestCheck);
        Assert.Equal(FindingEvidenceNegativeControl.RevertSrc, call.Control);
        Assert.Equal(paths, call.RevertPaths);
        Assert.Null(call.DeclaredPaths);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("default")]
    public void Request_MixedRepositories_BindsEachReceiptAndDoesNotReplay(string? homeRepository)
    {
        using var fixture = new Fixture();
        var homeFinding = Finding("home", new([new("Sample.Tests", "HomeTests")], Repository: homeRepository));
        var keyedFinding = Finding("keyed", new([new("./record", "TestReadInvalidFixtures")], Repository: "crosswalk"));
        FailReviewerNeedsWork(fixture.Kernel, fixture.Goal, fixture.Reviewer, "Need repository evidence", findings: [homeFinding, keyedFinding]);
        var homeCalls = new List<string>();
        var keyedCalls = new List<KeyedRepositoryEvidenceTarget>();
        var receipts = new List<FindingEvidenceReceipt>();
        var driver = fixture.Driver(receipts, homeCalls);
        driver.RunKeyedFocusedEvidence = (_, target, request, _, baseline, _, control, paths, mutation, _) =>
        {
            Assert.Equal(fixture.SecondaryWorktree, target.WorktreePath);
            Assert.Equal(fixture.ProjectHome, target.ProjectHomeDirectory);
            Assert.Equal("trunk", target.IntegrationBranch);
            Assert.True(baseline);
            Assert.Null(control);
            Assert.Null(paths);
            Assert.Null(mutation);
            keyedCalls.Add(target);
            return Green(request, target.CandidateHead);
        };

        Assert.True(driver.TryBuildFindingEvidenceRequest(fixture.Goal, fixture.Reviewer, ConductorAutonomyPolicy.Permissive, out _));
        Assert.Equal("tests/Sample.Tests/Sample.Tests.csproj:HomeTests", Assert.Single(homeCalls));
        Assert.Empty(keyedCalls);
        Assert.Equal(fixture.HomeHead, Assert.Single(receipts).CandidateSha);
        Assert.Equal(homeRepository, receipts[0].Request.Repository);

        Assert.True(driver.TryBuildFindingEvidenceRequest(fixture.Goal, fixture.Reviewer, ConductorAutonomyPolicy.Permissive, out _));
        Assert.Single(homeCalls);
        Assert.Equal(fixture.SecondaryHead, Assert.Single(keyedCalls).CandidateHead);
        Assert.Equal(2, receipts.Count);
        var keyed = receipts.Single(receipt => receipt.Request.Repository == "crosswalk");
        Assert.Equal(fixture.SecondaryHead, keyed.CandidateSha);
        Assert.NotEqual(fixture.HomeHead, keyed.CandidateSha);
        Assert.All(keyed.RequestDispositions!, disposition => Assert.Equal(fixture.SecondaryHead, disposition.CandidateSha));
        Assert.All(keyed.Arms!, arm => Assert.Equal(fixture.SecondaryHead, arm.Sha));

        Assert.False(driver.TryBuildFindingEvidenceRequest(fixture.Goal, fixture.Reviewer, ConductorAutonomyPolicy.Permissive, out _));
        Assert.Single(homeCalls);
        Assert.Single(keyedCalls);
        var attempt = driver._focusedEvidenceAttemptCoordinator.TryReadLatest(fixture.Goal.Id.Value)!;
        Assert.Equal("crosswalk", attempt.FocusedEvidenceRepository);
        Assert.Equal(fixture.SecondaryHead, attempt.BranchHeadSha);
        var replay = JsonSerializer.Deserialize<ConductorParallelAcceptanceAttempt>(JsonSerializer.Serialize(attempt))!;
        var restoredTarget = driver.ResolveAttemptEvidenceTarget(fixture.Goal, replay)!;
        Assert.Equal(fixture.SecondaryWorktree, restoredTarget.WorktreePath);
        Assert.Equal(fixture.ProjectHome, restoredTarget.ProjectHomeDirectory);
        Assert.Equal("trunk", restoredTarget.IntegrationBranch);
        Assert.Equal(fixture.SecondaryHead, restoredTarget.CandidateHead);
        driver.ResolveKeyedEvidenceTarget = (_, _) => (fixture.Resolve() with { CandidateHead = fixture.HomeHead }, string.Empty);
        Assert.Throws<InvalidOperationException>(() => driver.ResolveAttemptEvidenceTarget(fixture.Goal, replay));
    }

    [Fact]
    public void Request_MissingKeyedTarget_NeverInvokesHomeRunner()
    {
        using var fixture = new Fixture();
        FailReviewerNeedsWork(fixture.Kernel, fixture.Goal, fixture.Reviewer, "Need keyed evidence", findings:
            [Finding("keyed", new([new("./record", "TestReadInvalidFixtures")], Repository: "crosswalk"))]);
        var homeCalls = new List<string>();
        var receipts = new List<FindingEvidenceReceipt>();
        var driver = fixture.Driver(receipts, homeCalls);
        driver.ResolveKeyedEvidenceTarget = (_, _) => (null, "crosswalk:missing-worktree");
        driver.RunKeyedFocusedEvidence = (_, _, _, _, _, _, _, _, _, _) => throw new InvalidOperationException("Unresolved target executed");

        Assert.True(driver.TryBuildFindingEvidenceRequest(fixture.Goal, fixture.Reviewer, ConductorAutonomyPolicy.Permissive, out _));
        Assert.Empty(homeCalls);
        Assert.Empty(receipts);
        var outcome = Assert.Single(fixture.Reviewer.LastVerification!.MergedReviewFindings!).EvidenceOutcome!;
        Assert.False(outcome.Honoured);
        Assert.Equal(FindingEvidenceNotHonouredReason.ExecutorUnavailable, outcome.Reason);
    }

    [Fact]
    public void Resolve_MissingManifest_DoesNotBorrowHomeManifest()
    {
        using var fixture = new Fixture();
        File.Delete(Path.Combine(fixture.ProjectHome, "acceptance-manifest.json"));
        Assert.True(File.Exists(Path.Combine(fixture.HomeWorktree, "config", "acceptance-manifest.json")));
        var (target, failure) = KeyedRepositoryEvidenceTarget.TryResolve(fixture.Goal, "crosswalk", fixture.Registry);
        Assert.Null(target);
        Assert.NotEmpty(failure);
    }

    [Fact]
    public void Batch_IdenticalSelectionsInDifferentRepositories_RemainSeparate()
    {
        var request = new FindingEvidenceRequest([new("Sample.Tests", "ProbeTests")]);
        var groups = new string?[] { null, "default", "crosswalk" }.Select(key =>
        {
            var keyed = request with { Repository = key };
            return new FindingEvidenceRequestGroup(FindingEvidenceExecutionClassifier.BuildRequestIdentity(keyed),
                "Sample.Tests:ProbeTests", keyed, [Finding(key ?? "home", keyed)]);
        }).ToArray();
        var batches = FindingEvidenceRoundPlanner.BuildFindingEvidenceBatches(groups);
        Assert.Equal(3, batches.Count);
        Assert.Equal(new string?[] { null, "default", "crosswalk" }, batches.Select(batch => batch.TypedRequest.Repository));
    }

    [Fact]
    public void Attribute_KeyedRedWithoutBaseline_DoesNotReadHomeChanges()
    {
        var request = new FindingEvidenceRequest([new("Sample.Tests", "ProbeTests")], Repository: "crosswalk");
        var finding = Finding("keyed", request);
        var group = new FindingEvidenceRequestGroup("keyed", "Sample.Tests:ProbeTests", request, [finding]);
        var batch = Assert.Single(FindingEvidenceRoundPlanner.BuildFindingEvidenceBatches([group]));
        var driver = MakeDriver(getLandingFileScopes: _ => throw new InvalidOperationException("Keyed attribution read home changes"));
        FindingEvidenceArmReceipt[] arms = [new(FindingEvidenceArm.Candidate, "bbbbbbb", FindingEvidenceArmDisposition.Red,
            true, false, "red", FailingTestIdentities: ["ProbeTests.Fails"])];
        Assert.Null(driver.TryAttributeActionableCandidateRed(KeyedRepositoryEvidenceNormalizationTests.CreateGoal(), "bbbbbbb", arms, batch));
    }

    private static ReviewFinding Finding(string id, FindingEvidenceRequest request) =>
        new(id, ReviewFindingState.Open, new("src/Record.cs", "Record.Read"), "Prove repository evidence",
            EvidenceRequest: request, Category: FindingCategory.TestEvidence);

    private static FocusedEvidenceRunResult Green(string request, string head)
    {
        AcceptanceCheckResult[] checks = [new("focused tests", true, 0, "Executed: 1", ExecutedTestCount: 1)];
        return new(request, true, true, "focused check passed", checks,
            Arms: [new(FindingEvidenceArm.Candidate, head, FindingEvidenceArmDisposition.Green, true, true, "green", checks)],
            OutcomeReason: FindingEvidenceOutcomeReason.ValidEvidence);
    }

    private sealed class RecordingVerifier : IGoalAcceptanceVerifier
    {
        internal List<(string Worktree, AcceptanceFocusedVerificationOwner Owner, FindingEvidenceNegativeControl? Control,
            IReadOnlyList<string>? RevertPaths, IReadOnlyList<string>? DeclaredPaths)> Calls { get; } = [];
        public Task<AcceptanceVerificationResult> RunOwnedAsync(string worktreePath, GoalId? goalId, IReadOnlyList<string>? changedFiles,
            int? stableSlotIndex, DotnetBuildEnvironmentLease? stableSlotLease, IAcceptanceAttemptExecutionOwner executionOwner) =>
            throw new InvalidOperationException("Unexpected full acceptance run");
        public Task<FocusedEvidenceRunResult> RunFocusedEvidenceOwnedAsync(string worktreePath, GoalId? goalId, string request,
            IAcceptanceFocusedVerificationOwner executionOwner, int? stableSlotIndex = null, DotnetBuildEnvironmentLease? stableSlotLease = null,
            bool runBaselineArm = false) => Record(worktreePath, request, executionOwner, null, null, null);
        public Task<FocusedEvidenceRunResult> RunNegativeControlFocusedEvidenceOwnedAsync(string worktreePath, GoalId? goalId, string request,
            IAcceptanceFocusedVerificationOwner executionOwner, FindingEvidenceNegativeControl negativeControl, int? stableSlotIndex = null,
            DotnetBuildEnvironmentLease? stableSlotLease = null, bool runBaselineArm = false, IReadOnlyList<string>? revertPaths = null,
            FindingEvidenceMutation? mutation = null, IReadOnlyList<string>? declaredPaths = null) =>
            Record(worktreePath, request, executionOwner, negativeControl, revertPaths, declaredPaths);
        private Task<FocusedEvidenceRunResult> Record(string worktree, string request, IAcceptanceFocusedVerificationOwner owner,
            FindingEvidenceNegativeControl? control, IReadOnlyList<string>? paths, IReadOnlyList<string>? declared)
        {
            var typedOwner = Assert.IsType<AcceptanceFocusedVerificationOwner>(owner);
            Calls.Add((worktree, typedOwner, control, paths, declared));
            return Task.FromResult(Green(request, typedOwner.Identity.CandidateSha));
        }
    }

    private sealed class Fixture : IDisposable
    {
        internal GoalRepositoriesTestFixture Repositories { get; } = new();
        internal AgentOrchestratorKernel Kernel { get; }
        internal Goal Goal { get; }
        internal TaskSpec Reviewer => Goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        internal OrchestratorProjectRegistry Registry { get; }
        internal string HomeWorktree { get; }
        internal string SecondaryWorktree { get; }
        internal string ProjectHome { get; }
        internal string HomeHead { get; }
        internal string SecondaryHead { get; }

        internal Fixture()
        {
            Registry = new(Path.Combine(Repositories.Home.Root, "evidence-registry"), Path.Combine(Repositories.Home.Root, "evidence-data"));
            Repositories.Crosswalk.Git("checkout", "-b", "trunk");
            Registry.CreateProject("crosswalk", Repositories.Crosswalk.CheckoutPath, "trunk");
            ProjectHome = MultiRepositoryProjectVerifier.ProjectHome(Registry, "crosswalk");
            Directory.CreateDirectory(ProjectHome);
            File.WriteAllText(Path.Combine(ProjectHome, "acceptance-manifest.json"), GoPackageFocusedEvidenceTests.Manifest);
            var homeConfig = Path.Combine(Repositories.Home.CheckoutPath, "config");
            Directory.CreateDirectory(homeConfig);
            File.WriteAllText(Path.Combine(homeConfig, "acceptance-manifest.json"), """
                {"checks":[{"type":"dotnet-test","project":"tests/Sample.Tests/Sample.Tests.csproj"}]}
                """);
            Repositories.Home.Git("add", "config/acceptance-manifest.json");
            Repositories.Home.Git("commit", "-m", "Seed home settings");
            (Kernel, Goal) = SoftwareGoal();
            Kernel.DeclareGoalRepositories(Goal.Id, Repositories.Declaration);
            HomeWorktree = Repositories.CreateHomeWorktree(Goal);
            Assert.True(GoalRepositoryWorktreeProvisioner.TryProvision(new GoalRepositorySet(["crosswalk"]), Goal.Id,
                new Dictionary<string, string> { ["crosswalk"] = Repositories.Crosswalk.CheckoutPath }, out var bindings, out var failure), failure);
            SecondaryWorktree = bindings!["crosswalk"].WorktreePath;
            Assert.False(File.Exists(Path.Combine(SecondaryWorktree, "config", "acceptance-manifest.json")));
            HomeHead = GitFixture.CommitChange(HomeWorktree, "home candidate\n");
            SecondaryHead = GitFixture.CommitChange(SecondaryWorktree, "secondary candidate\n");
            Assert.NotEqual(HomeHead, SecondaryHead);
            foreach (var task in Goal.Tasks.Where(task => task.RequiredRole != AgentRole.Reviewer)) PassVerification(Kernel, Goal, task);
        }

        internal KeyedRepositoryEvidenceTarget Resolve()
        {
            var (target, failure) = KeyedRepositoryEvidenceTarget.TryResolve(Goal, "crosswalk", Registry);
            Assert.NotNull(target);
            Assert.Empty(failure);
            return target;
        }

        internal ConductorDriver Driver(List<FindingEvidenceReceipt> receipts, List<string> homeCalls)
        {
            var driver = MakeDriver(getPreReviewEvidenceContext: _ => NoPreReviewContext(HomeHead),
                getFindingEvidenceEngineSettings: _ => KeyedRepositoryEvidenceNormalizationTests.HomeSettings,
                focusedEvidenceAttemptCoordinator: new(Path.Combine(Repositories.Home.Root, "evidence-attempts"), "main", runInline: true),
                runFocusedEvidence: (_, request) => { homeCalls.Add(request); return Green(request, HomeHead); },
                recordFindingEvidenceOutcome: (goalId, taskId, id, outcome, receipt) =>
                {
                    if (receipt is not null) receipts.Add(receipt);
                    Kernel.RecordFindingEvidenceOutcome(goalId, taskId, id, outcome, receipt);
                });
            driver.ResolveKeyedEvidenceTarget = (goal, key) => KeyedRepositoryEvidenceTarget.TryResolve(goal, key, Registry);
            return driver;
        }

        public void Dispose() => Repositories.Dispose();
    }
}
