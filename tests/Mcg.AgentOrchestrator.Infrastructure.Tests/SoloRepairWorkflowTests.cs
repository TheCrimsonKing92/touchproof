using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using static AttributedFailureDeveloperRouteTests;
using static SoloAttributedFailureRouteFixture;

// Parallel-safe: Git, SQLite, artifact paths and the controlled verifier belong to this test.
public sealed class SoloRepairWorkflowTests
{
    private static readonly ConductorAutonomyPolicy Policy = ConductorAutonomyPolicy.Permissive with { MaxCriterionRetries = 0 };
    private const string LandingBlocker = "fixture stops at landing mutation";

    [Fact]
    public async Task RepairFlow_AfterLostAcknowledgement_ReusesRepairAndRegatesEachCandidate()
    {
        using var fixture = new SoloAttributedFailureRouteFixture();
        fixture.Git("checkout", "main");
        var oldMain = fixture.Git("rev-parse", "HEAD");
        var workspace = Workspace(fixture);
        var repository = new SqliteOrchestratorStateRepository(workspace.SqliteStatePath);
        var portfolio = new PortfolioStore(workspace.PortfolioStorePath);
        var maintenance = await portfolio.AddEpicAsync("baseline-maintenance");
        var firstEpic = await portfolio.AddEpicAsync("first project");
        var secondEpic = await portfolio.AddEpicAsync("second project");
        var first = NewCandidate(fixture, fixture.Kernel);
        var second = NewCandidate(fixture, fixture.Kernel);
        await portfolio.AssignGoalToEpicAsync(first.Id.Value, firstEpic.Id);
        await portfolio.AssignGoalToEpicAsync(second.Id.Value, secondEpic.Id);
        var third = NewCandidate(fixture, fixture.Kernel);
        var fourth = NewCandidate(fixture, fixture.Kernel);
        var goalIds = new[] { first.Id, second.Id, third.Id, fourth.Id };
        await repository.SaveAsync(fixture.Kernel);
        var verifier = new ControlledVerifier(fixture.Root);
        var driver = Driver(fixture.Kernel, workspace, verifier);
        var storePath = Path.Combine(workspace.OrchestratorDirectory, SoloBaselineRepairStore.FileName);
        var store = new SoloBaselineRepairStore(storePath);

        var firstFailure = await ProduceFailure(fixture, first);
        var interrupted = false;
        using (GoalCreationSideEffectDelivery.UseBeforeEffectDelivery(_ =>
        {
            interrupted = true;
            throw new IOException("lost repair creation acknowledgement");
        }))
        {
            AssertHold(Complete(driver, first, firstFailure), SoloHoldReasons.RepairPending);
        }
        Assert.True(interrupted);
        Assert.Equal(GoalStatus.AcceptanceFailed, first.Status);
        var finding = Assert.Single(store.ReadFindings(first.Id.Value));
        Assert.True(finding.Inherited, finding.Reason);
        Assert.Equal(oldMain, finding.Baseline);
        var intake = new GoalIntakeRequestStore(workspace.SqliteStatePath);
        var requestKey = "solo-baseline-repair-" + finding.GroupKey;
        var receipt = Assert.IsType<GoalIntakeRequestRecord>(intake.Get(requestKey));
        Assert.Equal(GoalIntakeRequestStates.Created, receipt.State);
        Assert.Null(store.ReadGroup(finding.GroupKey)?.RepairGoal);
        Assert.Empty(verifier.FocusedRuns);

        await repository.SaveAsync(fixture.Kernel);
        var kernel = await repository.LoadAsync();
        first = kernel.GetGoal(first.Id);
        second = kernel.GetGoal(second.Id);
        driver = Driver(kernel, workspace, verifier);
        AssertHold(driver.AdvanceOnce(first, Policy), SoloHoldReasons.RepairPending);
        Assert.Equal(receipt.GoalId, store.ReadGroup(finding.GroupKey)?.RepairGoal);
        var repair = kernel.GetGoal(new GoalId(receipt.GoalId!));
        Assert.Equal(maintenance.Id, (await portfolio.GetGoalMembershipAsync(repair.Id.Value))?.EpicId);

        foreach (var id in goalIds.Skip(1))
        {
            var goal = kernel.GetGoal(id);
            AssertHold(Complete(driver, goal, await ProduceFailure(fixture, goal)), SoloHoldReasons.RepairPending);
        }
        Assert.Equal(1, SoloRepairReplayTests.Count(storePath, "solo_repair_group"));
        Assert.Equal(4, SoloRepairReplayTests.Count(storePath, "solo_repair_dependency"));
        Assert.Equal(0, SoloRepairReplayTests.Count(storePath, "solo_repair_check"));
        Assert.Equal(firstEpic.Id, (await portfolio.GetGoalMembershipAsync(first.Id.Value))?.EpicId);
        Assert.Equal(secondEpic.Id, (await portfolio.GetGoalMembershipAsync(second.Id.Value))?.EpicId);

        GoalOperationJournal.RecordLandingIntent(fixture.Repo, repair, GoalWorktrees.BranchName(repair.Id),
            "main", oldMain, "fixture intent");
        AssertHold(driver.AdvanceOnce(first, Policy), SoloHoldReasons.RepairPending);
        fixture.Git("checkout", "-b", "unmerged-repair");
        fixture.Git("commit", "--allow-empty", "-m", $"Integrate goal/{repair.Id.Value}: repair baseline");
        fixture.Git("checkout", "main");
        AssertHold(driver.AdvanceOnce(first, Policy), SoloHoldReasons.RepairPending);
        Assert.Empty(verifier.FocusedRuns);

        fixture.Git("merge", "--ff-only", "unmerged-repair");
        fixture.Git("commit", "--allow-empty", "-m", "Advance integration after repair");
        var refreshedMain = fixture.Git("rev-parse", "HEAD");
        verifier.Finding = finding;
        foreach (var id in goalIds.Take(2))
        {
            var goal = kernel.GetGoal(id);
            RunGit(Worktree(fixture, goal), "rebase", "main");
            var resumed = Assert.IsType<ConductorAdvanceOutcome.Executed>(driver.AdvanceOnce(goal, Policy).Outcome);
            Assert.Equal("FreshGate", resumed.Decision?.Action);
            Assert.Equal(SoloHoldReasons.RepairValidated, resumed.Decision?.DiscriminatingEvidence);
            Assert.Contains(new PolicyDecisionFact("mainSha", refreshedMain), resumed.Decision!.Facts);
            Assert.Equal(GoalStatus.Verified, goal.Status);
        }
        Assert.Equal((finding.Request!, refreshedMain), Assert.Single(verifier.FocusedRuns));
        Assert.Empty(verifier.NormalRuns);
        store = new SoloBaselineRepairStore(storePath);
        var validation = Assert.IsType<SoloRepairCheckReceipt>(store.ReadCheck(
            SoloBaselineRepairStore.ValidationKey(finding, refreshedMain)));
        Assert.Equal("passed", validation.Outcome);
        Assert.NotEmpty(validation.Artifacts);

        var firstAdmission = Admit(driver, first);
        var secondAdmission = Admit(driver, second);
        var firstPass = RunNormal(fixture, verifier, firstAdmission, passed: true);
        var secondFailure = RunNormal(fixture, verifier, secondAdmission, passed: false);
        Assert.Equal(2, verifier.NormalRuns.Count);
        Assert.Equal(refreshedMain, firstPass.MainHeadSha);
        Assert.Equal(refreshedMain, secondFailure.MainHeadSha);
        Assert.Equal((first.Id, firstAdmission.BranchHeadSha!), verifier.NormalRuns[0]);
        Assert.Equal((second.Id, secondAdmission.BranchHeadSha!), verifier.NormalRuns[1]);
        Assert.NotEqual(firstPass.AttemptId, secondFailure.AttemptId);
        Assert.Null(store.ReadCheck(GateKey(firstAdmission)));
        Assert.Null(store.ReadCheck(GateKey(secondAdmission)));

        var atLanding = Assert.IsType<ConductorAdvanceOutcome.Held>(Complete(driver, first, firstPass).Outcome);
        Assert.Contains(LandingBlocker, atLanding.Reason);
        Assert.Equal(firstPass.AttemptId, store.ReadCheck(GateKey(firstAdmission))?.Detail);
        var rejected = Assert.IsType<ConductorAdvanceOutcome.Escalated>(Complete(driver, second, secondFailure).Outcome);
        Assert.Contains("normal candidate acceptance", rejected.Reason);
        Assert.Null(store.ReadCheck(GateKey(secondAdmission)));

        Assert.Contains(LandingBlocker, Assert.IsType<ConductorAdvanceOutcome.Held>(
            Complete(driver, first, firstPass).Outcome).Reason);
        Assert.Equal(2, SoloRepairReplayTests.Count(storePath, "solo_repair_check"));
        Assert.Single(verifier.FocusedRuns);

        fixture.Git("commit", "--allow-empty", "-m", "Move integration before another candidate gate");
        Assert.Null(driver.TryBuildParallelAcceptanceCandidate(first, Policy, 0, out var decline));
        Assert.Equal(SoloHoldReasons.RepairPending, decline);
        Assert.Null(driver.TryBuildParallelAcceptanceCandidate(second, Policy, 1, out _));
        Assert.Single(verifier.FocusedRuns);

        var otherBase = NewCandidate(fixture, kernel);
        AssertHold(Complete(driver, otherBase, await ProduceFailure(fixture, otherBase)), SoloHoldReasons.RepairPending);
        var otherFinding = Assert.Single(store.ReadFindings(otherBase.Id.Value));
        Assert.NotEqual(finding.GroupKey, otherFinding.GroupKey);
        Assert.Equal(fixture.Git("rev-parse", "HEAD"), otherFinding.Baseline);
        Assert.NotEqual(repair.Id.Value, store.ReadGroup(otherFinding.GroupKey)?.RepairGoal);
        Assert.Equal(2, SoloRepairReplayTests.Count(storePath, "solo_repair_group"));
        Assert.Equal(5, SoloRepairReplayTests.Count(storePath, "solo_repair_dependency"));
    }

    internal static OrchestratorWorkspace Workspace(SoloAttributedFailureRouteFixture fixture)
    {
        var workspace = OrchestratorWorkspace.ForProject("solo-repair-test", fixture.Repo, integrationBranch: "main",
            dataRootDirectory: Path.Combine(fixture.Root, "data"));
        Directory.CreateDirectory(workspace.OrchestratorDirectory);
        _ = StateDbMigrations.EnsureUpToDate(workspace.SqliteStatePath);
        AgentCatalogStore.Save(workspace.AgentCatalogPath, AgentCatalog.Default());
        return workspace;
    }

    private static ConductorDriver Driver(AgentOrchestratorKernel kernel, OrchestratorWorkspace workspace,
        ControlledVerifier verifier)
    {
        var driver = new ConductorDriver(kernel, workspace, verifier, AgentCatalog.Default().Agents,
            WorkerProfileCatalog.Default(), runAcceptanceAttemptsInCurrentProcess: true,
            cleanupHooks: new GoalWorktreeCleanupHooks
            {
                BuildStorageRoot = new DotnetBuildStorageRoot(Path.Combine(workspace.OrchestratorDirectory, "build"))
            })
        {
            CohortPartitionStableSlotLeaseSource = (_, _) =>
                AcceptanceStableSlotTestSupport.CreateFakeStableSlotLease(workspace.OrchestratorDirectory),
            LandingMutationBlocker = () => LandingBlocker
        };
        driver.BeginTick(kernel, 1);
        return driver;
    }

    private static Goal NewCandidate(SoloAttributedFailureRouteFixture fixture, AgentOrchestratorKernel kernel)
    {
        var goal = CompletedGoal(kernel, fixture.Repo);
        var worktree = GoalWorktrees.Ensure(fixture.Repo, goal.Id);
        File.WriteAllText(Path.Combine(worktree, "README.md"), goal.Id.Value);
        RunGit(worktree, "add", "README.md");
        RunGit(worktree, "commit", "-m", "Candidate changes");
        return goal;
    }

    private static string Worktree(SoloAttributedFailureRouteFixture fixture, Goal goal) =>
        Assert.IsType<string>(GoalWorktrees.TryResolve(fixture.Repo, goal.Id));

    private static string RunGit(string directory, params string[] arguments)
    {
        var result = GitCli.Run(directory, arguments);
        Assert.True(result.Succeeded, result.Error);
        return result.Output.Trim();
    }

    private static async Task<AcceptanceVerificationSummary> ProduceFailure(SoloAttributedFailureRouteFixture fixture, Goal goal)
    {
        await fixture.Produce(baselineFails: true, attemptId: goal.Id.Value, candidateWorktree: Worktree(fixture, goal),
            goal: goal, singleRow: true);
        return fixture.Summary;
    }

    private static ConductorAdvanceResult Complete(ConductorDriver driver, Goal goal, AcceptanceVerificationSummary summary) =>
        driver.CompleteParallelLandingAcceptance(
            ConductorParallelAcceptanceCandidate.Create(goal, 0, ["README.md"], summary.BranchHeadSha, summary.MainHeadSha),
            Policy, summary, out _);

    private static void AssertHold(ConductorAdvanceResult result, string reason)
    {
        var hold = Assert.IsType<ConductorAdvanceOutcome.Held>(result.Outcome);
        Assert.Equal(reason, hold.Decision?.DiscriminatingEvidence);
        Assert.Contains(new PolicyDecisionFact("owner", "conductor"), hold.Decision!.Facts);
    }

    private static ConductorParallelAcceptanceCandidate Admit(ConductorDriver driver, Goal goal)
    {
        var candidate = driver.TryBuildParallelAcceptanceCandidate(goal, Policy, 0, out var reason);
        Assert.Null(reason);
        return Assert.IsType<ConductorParallelAcceptanceCandidate>(candidate);
    }

    private static string GateKey(ConductorParallelAcceptanceCandidate admitted) =>
        SoloBaselineRepairStore.NormalGateKey(admitted.Goal.Id.Value, admitted.BranchHeadSha!, admitted.MainHeadSha!);

    private static AcceptanceVerificationSummary RunNormal(SoloAttributedFailureRouteFixture fixture,
        ControlledVerifier verifier, ConductorParallelAcceptanceCandidate admitted, bool passed)
    {
        verifier.NormalPass = passed;
        var verification = AcceptanceExecutionRunner.RunAttempt(verifier, Worktree(fixture, admitted.Goal), admitted.Goal.Id,
            ["README.md"], null, null, CancellationToken.None,
            new(RunId: "normal-" + admitted.Goal.Id.Value, ResultsPrefix: Path.Combine(fixture.Root, "normal", admitted.Goal.Id.Value),
                IntegrationBranch: "main"));
        Assert.Equal(passed, verification.Passed);
        var failed = verification.Checks!.Where(check => !check.Passed).ToArray();
        return AcceptanceVerificationSummary.FromVerification(verification, failed, failed.Select(check => check.Name).ToArray(),
            admitted.BranchHeadSha, admitted.MainHeadSha, verification.TestResultPaths, null, null);
    }

    internal sealed class ControlledVerifier(string root) : IGoalAcceptanceVerifier
    {
        internal SoloRepairFinding? Finding { get; set; }
        internal List<(string Request, string Sha)> FocusedRuns { get; } = [];
        internal List<(GoalId Goal, string Sha)> NormalRuns { get; } = [];
        internal bool NormalPass { get; set; }

        public Task<FocusedEvidenceRunResult> RunFocusedEvidenceOwnedAsync(string worktreePath, GoalId? goalId,
            string request, IAcceptanceFocusedVerificationOwner executionOwner, int? stableSlotIndex = null,
            DotnetBuildEnvironmentLease? stableSlotLease = null, bool runBaselineArm = false)
        {
            var finding = Assert.IsType<SoloRepairFinding>(Finding);
            Assert.Equal(finding.Request, request);
            Assert.False(runBaselineArm);
            Assert.NotNull(stableSlotLease);
            var sha = RunGit(worktreePath, "rev-parse", "HEAD");
            FocusedRuns.Add((request, sha));
            var trx = Path.Combine(root, sha + ".trx");
            WriteTrx(trx, true, finding.Identity);
            return Task.FromResult(SoloRepairEvidenceTests.FocusedResult(finding, sha, trx, true, true, finding.Identity));
        }

        public Task<AcceptanceVerificationResult> RunOwnedAsync(string worktreePath, GoalId? goalId,
            IReadOnlyList<string>? changedFiles, int? stableSlotIndex, DotnetBuildEnvironmentLease? stableSlotLease,
            IAcceptanceAttemptExecutionOwner executionOwner)
        {
            var id = Assert.IsType<GoalId>(goalId);
            NormalRuns.Add((id, RunGit(worktreePath, "rev-parse", "HEAD")));
            var check = new AcceptanceCheckResult("normal candidate acceptance", NormalPass, NormalPass ? 0 : 1,
                NormalPass ? "pass" : "branch-local failure");
            return Task.FromResult(new AcceptanceVerificationResult(NormalPass, false, check.ExitCode, check.OutputTail,
                Checks: [check]));
        }
    }
}
