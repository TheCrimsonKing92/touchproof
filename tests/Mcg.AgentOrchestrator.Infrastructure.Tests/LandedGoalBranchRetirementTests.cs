using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each call owns its repository, journal, SQLite stores, kernel and cleanup hooks.
public sealed class LandedGoalBranchRetirementTests : CliCommandTestBase
{
    [Fact]
    public void MatchingTrainReceipt_ReturnsTipWithOneAncestryLaunch() => Check(expectTip: true);

    [Fact]
    public void MatchingCohortReceipt_ReturnsTipWithOneAncestryLaunch() => Check(expectTip: true, cohort: true);

    [Fact]
    public void CancelledGoal_ReturnsNull() => Check(status: GoalStatus.Cancelled, expectedCheck: "status");

    [Fact]
    public void VerifiedGoal_MatchingTrainReceipt_ReturnsTip() =>
        Check(expectTip: true, status: GoalStatus.Verified, finalized: true);

    [Fact]
    public void VerifiedGoal_MatchingCohortReceipt_ReturnsTip() =>
        Check(expectTip: true, cohort: true, status: GoalStatus.Verified, finalized: true);

    [Fact]
    public void VerifiedEquivalentBranch_MatchingReceipt_ReturnsTip() =>
        Check(expectTip: true, status: GoalStatus.Verified,
            changeFacts: facts => facts with { ContentState = GoalBranchContentState.EquivalentToMain });

    [Fact]
    public void MissingBranch_ReturnsNull() =>
        Check(changeFacts: facts => facts with { HasGoalBranch = false }, expectedCheck: "missing-branch");

    [Fact]
    public void MergedBranch_ReturnsNull() =>
        Check(changeFacts: facts => facts with { BranchAlreadyLanded = true }, expectedCheck: "branch-already-landed");

    [Fact]
    public void EquivalentBranch_ReturnsNull() =>
        Check(changeFacts: facts => facts with { ContentState = GoalBranchContentState.EquivalentToMain },
            expectedCheck: "content-equivalent");

    [Fact]
    public void MissingLandingIntent_ReturnsNull() => Check(withIntent: false, expectedCheck: "missing-landing-intent");

    [Fact]
    public void TombstonedLandingIntent_ReturnsNull() => Check(tombstoneIntent: true, expectedCheck: "missing-landing-intent");

    [Fact]
    public void LandingCommitOffMain_ReturnsNull() => Check(ancestryExit: 1, expectedCheck: "landing-commit-not-on-main");

    [Fact]
    public void AncestryCommandFailure_ReturnsNull() => Check(ancestryExit: 128, expectedCheck: "landing-commit-not-on-main");

    [Fact]
    public void StaleReceiptCandidate_ReturnsNull() => Check(staleCandidate: true, expectedCheck: "no-matching-receipt");

    [Fact]
    public void NoPassedReceipt_ReturnsNull() => Check(withReceipt: false, expectedCheck: "no-matching-receipt");

    [Fact]
    public void FailedReceipt_ReturnsNull() => Check(passedReceipt: false, expectedCheck: "no-matching-receipt");

    [Fact]
    public void MissingTip_ReturnsNull() => Check(withTip: false, expectedCheck: "missing-tip");

    [Fact]
    public void MissingMainRevision_ReturnsNull() => Check(withMain: false, expectedCheck: "missing-main");

    [Fact]
    public void VerifiedGoal_MissingLandingIntent_ReturnsNull() =>
        Check(status: GoalStatus.Verified, withIntent: false, expectedCheck: "missing-landing-intent");

    [Fact]
    public void VerifiedGoal_LandingCommitOffMain_ReturnsNull() =>
        Check(status: GoalStatus.Verified, ancestryExit: 1, expectedCheck: "landing-commit-not-on-main");

    [Fact]
    public void VerifiedGoal_StaleReceiptCandidate_ReturnsNull() =>
        Check(status: GoalStatus.Verified, staleCandidate: true, expectedCheck: "no-matching-receipt");

    [Fact]
    public void FirstFailure_MissingBranchPrecedesMissingTip() =>
        Check(withTip: false, changeFacts: facts => facts with { HasGoalBranch = false }, expectedCheck: "missing-branch");

    [Fact]
    public void FirstFailure_FinalizedAncestryPrecedesStaleCandidate() =>
        Check(finalized: true, staleCandidate: true, ancestryExit: 1, expectedCheck: "landing-commit-not-on-main");

    [Fact]
    public void FinalizedStaleCandidate_ReturnsNullAfterOneAncestryLaunch() =>
        Check(finalized: true, staleCandidate: true, expectedCheck: "no-matching-receipt");

    [Fact]
    public void Sweep_VerifiedTwoCommitRebasedMember_RetiresThroughLandedProof() => CheckSweep();

    [Fact]
    public void Sweep_VerifiedCohortMember_RetiresThroughLandedProof() => CheckSweep(cohort: true);

    [Fact]
    public void Sweep_VerifiedEquivalentWithoutLandingIntent_BlockerNamesMissingLandingIntent() =>
        CheckSweep(expectedCheck: "missing-landing-intent");

    [Fact]
    public void Sweep_VerifiedEquivalentLandingCommitOffMain_BlockerNamesLandingCommitNotOnMain() =>
        CheckSweep(expectedCheck: "landing-commit-not-on-main");

    [Fact]
    public void Sweep_VerifiedEquivalentStaleReceiptCandidate_BlockerNamesNoMatchingReceipt() =>
        CheckSweep(expectedCheck: "no-matching-receipt");

    [Fact]
    public void Sweep_CompletionRefused_LeavesVerifiedBranchIntact() => CheckSweep(expectedCheck: "completion-refused");

    private void CheckSweep(string? expectedCheck = null, bool cohort = false)
    {
        var root = CreateAcceptanceRepository();
        GoalId? cleanupGoalId = null;
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var task = new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer);
            var goal = kernel.CreateGoal("Retire rebased train member", [task]);
            cleanupGoalId = goal.Id;
            kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
            kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Completed, "done");
            kernel.RecordTaskVerification(goal.Id, task.Id, new TaskVerificationRecord(
                "manual", root, 0, "passed", string.Empty, DateTimeOffset.UtcNow));
            kernel = WithGoalStatus(kernel, goal.Id, GoalStatus.Verified);
            goal = kernel.GetGoal(goal.Id);
            var branch = GoalWorktrees.BranchName(goal.Id);
            var worktree = CommitGoalWork(root, goal.Id, "member.txt", "first change", useTemplate: false);
            File.AppendAllText(Path.Combine(worktree, "member.txt"), "\nsecond change");
            RunGit(worktree, "add", "member.txt");
            RunGit(worktree, "commit", "-m", "Second member commit");
            var originalTip = RunGitOutput(worktree, "rev-parse", "HEAD").Trim();

            File.WriteAllText(Path.Combine(root, "main-only.txt"), "advance main before rebasing");
            RunGit(root, "add", "main-only.txt");
            RunGit(root, "commit", "-m", "Advance main");
            RunGit(worktree, "rebase", "main");
            var tip = RunGitOutput(worktree, "rev-parse", "HEAD").Trim();
            var first = RunGitOutput(worktree, "rev-parse", "HEAD^").Trim();
            Assert.NotEqual(originalTip, tip);

            // Distinct landing messages force different commit identities without timing assumptions.
            RunGit(root, "cherry-pick", "--no-commit", first);
            RunGit(root, "commit", "-m", "Land first member change");
            RunGit(root, "cherry-pick", "--no-commit", tip);
            RunGit(root, "commit", "-m", "Land second member change");
            var landing = RunGitOutput(root, "rev-parse", "HEAD").Trim();
            var cherry = RunGitOutput(root, "cherry", "main", branch)
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            Assert.Equal(2, cherry.Length);
            Assert.All(cherry, line => Assert.StartsWith("- ", line, StringComparison.Ordinal));
            Assert.Equal(1, GitCli.Run(root, "merge-base", "--is-ancestor", tip, "main").ExitCode);
            Assert.Empty(RunGitOutput(worktree, "status", "--porcelain").Trim());

            if (expectedCheck == "completion-refused")
            {
                kernel.RecordGoalRefinement(goal.Id, new RefinedSpec(
                    goal.Objective, ["Operator confirms retirement"], VerificationClass.TestVerifiable, [], []));
                kernel.MapCriterionEvidenceOwner(goal.Id, 0, 1, CriterionEvidenceOwner.Operator, "test", "manual",
                    expectedCandidateSha: tip);
                Assert.Single(goal.LandingPrerequisiteObligations);
            }
            var intentCommit = expectedCheck == "landing-commit-not-on-main" ? tip : landing;
            if (expectedCheck != "missing-landing-intent")
                GoalOperationJournal.RecordLandingIntent(root, goal, branch, "main", intentCommit, "test");
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            var trainStore = new MergeTrainAcceptanceStore(Path.Combine(workspace.OrchestratorDirectory, "train.db"));
            var cohortStore = new CohortAcceptanceStore(Path.Combine(workspace.OrchestratorDirectory, "cohort.db"));
            var candidate = expectedCheck == "no-matching-receipt" ? first : tip;
            SaveReceipt(root, goal.Id, candidate, landing, trainStore, cohortStore, cohort: cohort, passed: true,
                landing: intentCommit, mainTree: RunGitOutput(root, "rev-parse", "main^{tree}").Trim());
            Assert.Equal(candidate, Assert.Single(cohort
                ? cohortStore.ReadFinalizedLandingsForGoal(goal.Id)
                : trainStore.ReadFinalizedLandingsForGoal(goal.Id)).CandidateRevision);
            var hooks = CreateIsolatedCleanupContext(workspace, new GoalWorktreeCleanupHooks
            {
                ResetSandboxAcl = (_, _) => { },
                FindLockHoldersForCleanup = _ => [],
                ProcessCommandLineSnapshot = _ => new ProcessCommandLineSnapshot(new Dictionary<int, string>())
            }).Hooks;

            var result = Assert.Single(TerminalGoalSweep.Run(kernel, root, "main", goal.Id,
                cleanupHooks: hooks, mergeTrainAcceptanceStore: trainStore, cohortAcceptanceStore: cohortStore).Goals);

            if (expectedCheck is null)
            {
                Assert.Empty(result.Blockers);
                var repair = Assert.Single(result.Repairs, item => item.Kind == "completed-branch-landed-retired");
                Assert.Contains($"receipt={(cohort ? "cohort-receipt" : "merge-train-receipt")}:receipt",
                    repair.Evidence, StringComparison.Ordinal);
                Assert.Contains($"candidateRevision={tip}", repair.Evidence, StringComparison.Ordinal);
                Assert.Contains($"landingCommit={landing}", repair.Evidence, StringComparison.Ordinal);
                Assert.Equal(GoalStatus.Completed, kernel.GetGoal(goal.Id).Status);
                Assert.DoesNotContain(result.Blockers, item => item.Kind == "completed-branch-superseded");
                Assert.NotEqual(0, GitCli.Run(root, "show-ref", "--verify", $"refs/heads/{branch}").ExitCode);
                Assert.False(Directory.Exists(worktree));
                Assert.DoesNotContain(worktree.Replace('\\', '/'),
                    RunGitOutput(root, "worktree", "list", "--porcelain").Replace('\\', '/'), StringComparison.OrdinalIgnoreCase);
                Assert.True(GoalOperationJournal.HasRetiredTerminalDisposition(GoalOperationJournal.Read(root, goal.Id)));
            }
            else
            {
                var blocker = Assert.Single(result.Blockers);
                Assert.Equal("completed-branch-superseded", blocker.Kind);
                Assert.Contains("cleanup=retirement required", blocker.Evidence, StringComparison.Ordinal);
                Assert.Contains($"landedProofCheck={expectedCheck}", blocker.Evidence, StringComparison.Ordinal);
                Assert.Equal(GoalStatus.Verified, kernel.GetGoal(goal.Id).Status);
                Assert.Equal(tip, RunGitOutput(root, "rev-parse", branch).Trim());
                Assert.True(Directory.Exists(worktree));
                Assert.DoesNotContain(result.Repairs, item => item.Kind == "completed-branch-landed-retired");
                Assert.False(GoalOperationJournal.HasRetiredTerminalDisposition(GoalOperationJournal.Read(root, goal.Id)));
            }
        }
        finally { CleanupAcceptanceRepository(root, cleanupGoalId); }
    }

    private static void Check(
        bool expectTip = false, bool cohort = false, GoalStatus status = GoalStatus.Completed,
        Func<GoalBranchFacts, GoalBranchFacts>? changeFacts = null,
        bool withIntent = true, bool tombstoneIntent = false, int ancestryExit = 0,
        bool staleCandidate = false, bool withReceipt = true, bool passedReceipt = true,
        bool withTip = true, bool withMain = true, string? expectedCheck = null, bool finalized = false)
    {
        var root = CreateTempDirectory();
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var created = kernel.CreateGoal("Retire proven branch", [new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer)]);
            kernel = WithGoalStatus(kernel, created.Id, status);
            var goal = kernel.GetGoal(created.Id);
            var tip = new string('1', 40);
            var landing = new string('2', 40);
            var main = new string('3', 40);
            if (withIntent)
                GoalOperationJournal.RecordLandingIntent(root, goal, GoalWorktrees.BranchName(goal.Id), "main", landing, "test");
            if (tombstoneIntent)
                GoalOperationJournal.TombstoneLandingIntent(root, goal, "intent revoked");
            var trainStore = new MergeTrainAcceptanceStore(Path.Combine(root, "train.db"));
            var cohortStore = new CohortAcceptanceStore(Path.Combine(root, "cohort.db"));
            var candidate = staleCandidate ? new string('4', 40) : tip;
            if (withReceipt)
                SaveReceipt(root, goal.Id, candidate, main, trainStore, cohortStore, cohort, passedReceipt,
                    finalized ? landing : null);
            var facts = new GoalBranchFacts(true, false, true, true, true, false, GoalBranchContentState.AbsentFromMain);
            var commands = new List<string[]>();
            GitCli.GitResult RunGit(string directory, IReadOnlyList<string> args)
            {
                Assert.Equal(root, directory);
                commands.Add(args.ToArray());
                Assert.Equal(new[] { "merge-base", "--is-ancestor", landing, main }, args);
                return new(ancestryExit, "", ancestryExit > 1 ? "git failure" : "");
            }

            var actual = LandedGoalBranchRetirement.TryGetLandedBranchTip(
                goal, changeFacts?.Invoke(facts) ?? facts, withTip ? tip : null, withMain ? main : null,
                GoalOperationJournal.Read(root, goal.Id), trainStore, cohortStore, root, RunGit,
                out var proof, out var failingCheck);

            Assert.Equal(expectedCheck, failingCheck);
            Assert.InRange(commands.Count, 0, 1);
            if (expectTip)
            {
                Assert.Equal(tip, actual);
                Assert.Single(commands);
                Assert.NotNull(proof);
                Assert.Equal(cohort ? "cohort-receipt" : "merge-train-receipt", proof.ReceiptKind);
                Assert.Equal("receipt", proof.ReceiptId);
                Assert.Equal(tip, proof.CandidateRevision);
                Assert.Equal(landing, proof.LandingCommit);
            }
            else
            {
                Assert.Null(actual);
                Assert.Null(proof);
                if (ancestryExit != 0 || finalized && staleCandidate)
                    Assert.Single(commands);
                else
                    Assert.Empty(commands);
            }
        }
        finally { SharedTestSupport.RemoveTempDirectory(root); }
    }

    private static void SaveReceipt(
        string root, GoalId goalId, string candidate, string main,
        MergeTrainAcceptanceStore trainStore, CohortAcceptanceStore cohortStore, bool cohort, bool passed,
        string? landing = null, string? mainTree = null)
    {
        var trx = Path.Combine(root, ".orchestrator", "pass.trx");
        Directory.CreateDirectory(Path.GetDirectoryName(trx)!);
        File.WriteAllText(trx, """
            <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
              <Results><UnitTestResult testId="1" testName="Passes" outcome="Passed" /></Results>
              <ResultSummary outcome="Completed"><Counters total="1" executed="1" passed="1" failed="0" /></ResultSummary>
            </TestRun>
            """);
        var other = GoalId.New();
        if (cohort)
        {
            AcceptanceCohortMemberBinding Member(GoalId id, string path) => new(
                id, candidate, candidate, [path], [path], ChangeRiskTier.Behavior,
                ConductorTransitionDecision.Auto, GateReadyMergeStatus.Clean.ToString(), GateReadyMergeReason.NoConflictsDetected.ToString());
            var identity = AcceptanceCohortIdentity.Create([Member(goalId, "src/A.cs"), Member(other, "src/B.cs")], main, mainTree ?? main, "manifest-v1");
            var receipt = cohortStore.SaveGateReceipt(new AcceptanceCohortReceipt("receipt", identity,
                passed ? AcceptanceCohortGateOutcome.Passed : AcceptanceCohortGateOutcome.Failed,
                DateTimeOffset.UtcNow, 10, [], passed ? 0 : 1, [trx], ValidForLanding: passed));
            if (landing is not null)
            {
                cohortStore.PrepareLanding(receipt, landing);
                Assert.Equal(2, cohortStore.FinalizeLanding(identity.Value, receipt.ReceiptId).Count);
                cohortStore.CompleteLandingEffects(identity.Value, receipt.ReceiptId);
            }
        }
        else
        {
            MergeTrainMemberBinding Member(GoalId id, string path) => new MergeTrainMemberBinding(
                id, candidate, candidate, [path], [path], ChangeRiskTier.Behavior,
                ConductorTransitionDecision.Auto, GateReadyMergeStatus.Clean.ToString(), GateReadyMergeReason.NoConflictsDetected.ToString())
                .WithRebasedHead(candidate);
            var identity = MergeTrainIdentity.Create([Member(goalId, "src/A.cs"), Member(other, "src/B.cs")], main, mainTree ?? main, "manifest-v1");
            var receipt = trainStore.SaveGateReceipt(new MergeTrainReceipt("receipt", identity,
                passed ? MergeTrainGateOutcome.Passed : MergeTrainGateOutcome.Failed,
                DateTimeOffset.UtcNow, 10, [], passed ? 0 : 1, [trx], ValidForLanding: passed));
            if (landing is not null)
            {
                trainStore.PrepareLanding(receipt, landing, null);
                Assert.Equal(2, trainStore.FinalizeLanding(identity.Value, receipt.ReceiptId).Count);
                trainStore.CompleteLandingEffects(identity.Value, receipt.ReceiptId);
            }
        }
    }
}
