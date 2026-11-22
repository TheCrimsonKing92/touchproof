using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

using static LandingExecutorTests;

[Xunit.Collection(TestCollections.DotnetBuildSlots)]
public sealed class ConductorBatchLoopTestsRefusedCarryRelaunch : ConductorBatchLoopTests
{
    public ConductorBatchLoopTestsRefusedCarryRelaunch(ITestOutputHelper output) : base(output) { }

    [Xunit.Theory(Timeout = TestHangGuard.WholeTestMilliseconds)]
    [Xunit.Trait("Category", "CrossTick")]
    [Xunit.InlineData(false, "rebind")]
    [Xunit.InlineData(true, "rebind")]
    [Xunit.InlineData(false, "new-brief")]
    [Xunit.InlineData(false, "evidence")]
    [Xunit.InlineData(false, "evidence-ready")]
    [Xunit.InlineData(false, "partial")]
    [Xunit.InlineData(false, "exact-replay")]
    public void RefusedCarryDefersAcrossHeadMovementAndReleasesForLivePair(bool moveBranch, string repair)
    {
        var repository = CreateGitRepository();
        var attemptRoot = CreateTempDirectory("mcg-refused-carry-attempts");
        try
        {
            // A held receipt is compared with the effective manifest on later ticks,
            // even though this fixture substitutes the acceptance runner.
            AppendCommit(repository, "config/acceptance-manifest.json", """
                { "version": 1, "checks": [{ "name": "check", "type": "command", "command": "git", "arguments": ["diff", "--check"] }] }
                """);
            var baseSha = ReadGit(repository, "rev-parse", "main");
            ReadGit(repository, "checkout", "-b", "candidate-old");
            AppendCommit(repository, "src/goal.txt", "reviewed goal line");
            var boundSha = ReadGit(repository, "rev-parse", "HEAD");
            ReadGit(repository, "checkout", "main");
            if (repair == "evidence-ready")
            {
                AppendCommit(repository, "src/disjoint.txt", "different candidate base");
                baseSha = ReadGit(repository, "rev-parse", "main");
            }
            ReadGit(repository, "checkout", "-b", "candidate-new", baseSha);
            AppendCommit(repository, "src/goal.txt", repair == "evidence-ready" ? "reviewed goal line" : "changed goal line");
            var candidateSha = ReadGit(repository, "rev-parse", "HEAD");
            var mainSha = ReadGit(repository, "rev-parse", "main");

            var kernel = new AgentOrchestratorKernel();
            var goal = CreateVerifiedSimpleGoal(kernel, "Update src/goal.txt");
            string[] criteria = repair == "partial"
                ? ["Full acceptance passes", "Second acceptance obligation"]
                : ["Full acceptance passes"];
            kernel.RecordGoalRefinement(goal.Id, new RefinedSpec(
                goal.Objective, criteria, VerificationClass.TestVerifiable, [], []));
            kernel.MapCriterionEvidenceOwner(goal.Id, 0, 1, CriterionEvidenceOwner.Acceptance,
                "reviewer", CriterionEvidenceScopes.FullAcceptanceGate, expectedCandidateSha: boundSha);
            if (repair == "partial")
                kernel.MapCriterionEvidenceOwner(goal.Id, 1, 1, CriterionEvidenceOwner.Acceptance,
                    "reviewer", CriterionEvidenceScopes.FullAcceptanceGate, expectedCandidateSha: boundSha);
            var obligation = goal.CriterionEvidenceObligations.Single(item => item.CriterionIndex == 0);
            if (repair == "evidence-ready")
                RecordEvidence(boundSha, "failed-old-branch", passed: false);
            var worktree = GoalWorktrees.WorktreePath(repository, goal.Id);
            Directory.CreateDirectory(Path.GetDirectoryName(worktree)!);
            ReadGit(repository, "worktree", "add", "-b", GoalWorktrees.BranchName(goal.Id),
                worktree, candidateSha);
            ReadGit(repository, "checkout", "main");

            var runs = new List<(string Branch, string Main)>();
            var trxPath = Path.Combine(attemptRoot, "acceptance.trx");
            File.WriteAllText(trxPath, """
                <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
                  <Results><UnitTestResult testName="Fixture.Check" outcome="Passed" /></Results>
                  <ResultSummary outcome="Completed"><Counters total="1" executed="1" passed="1" failed="0" /></ResultSummary>
                </TestRun>
                """);
            var coordinator = new ConductorParallelAcceptanceAttemptCoordinator(
                attemptRoot, Mcg.AgentOrchestrator.Infrastructure.TrunkBranchName.Default, repository, runInline: true);
            var driver = MakeDriver(
                getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
                runAcceptanceWithSlot: (_, _) =>
                {
                    runs.Add((candidateSha, mainSha));
                    return new AcceptanceVerificationSummary(true, [], BranchHeadSha: candidateSha, MainHeadSha: mainSha,
                        TestResultPaths: [trxPath]);
                },
                resolveAcceptanceHeads: _ => (candidateSha, mainSha),
                getLandingFileScopes: _ => ["src/goal.txt"],
                executionDirectory: repository,
                parallelAcceptanceAttemptCoordinator: coordinator);

            var first = new ConductorBatchLoop().Run(
                kernel, driver, ConductorAutonomyPolicy.Conservative, NoStopPath(), maxIterations: 1);
            Assert.Equal((candidateSha, mainSha), Assert.Single(runs));
            Assert.Equal(1, first.Held);
            Assert.Equal(GoalStatus.Verified, goal.Status);
            var passedReceipt = Assert.IsType<ConductorParallelAcceptanceAttempt>(coordinator.TryReadLatest(goal.Id.Value));
            Assert.Equal(ConductorParallelAcceptanceAttemptOutcome.Passed, passedReceipt.Outcome);
            Assert.Equal(GoalAcceptanceVerifier.ComputeEffectiveAcceptancePlanIdentity(worktree, ["src/goal.txt"]),
                passedReceipt.EffectiveManifestIdentity);
            var expectedRefusal = repair == "evidence-ready" ? "not-eligible" : $"range-diff-not-identical; head={boundSha}";
            var expectedDiagnostic =
                $"Acceptance passed for {candidateSha}, but obligation '{obligation.Id}' is bound to {boundSha}. Rebind the obligation to the current candidate before recording its evidence. Carry-forward refused: reason={expectedRefusal}.";
            Assert.StartsWith(expectedDiagnostic,
                AcceptanceCriterionEvidence.RecordAndDescribeOutstanding(
                    goal, candidateSha, kernel, Mcg.AgentOrchestrator.Infrastructure.TrunkBranchName.Default, repository),
                StringComparison.Ordinal);

            for (var tick = 0; tick < 2; tick++)
            {
                BatchTickSummary? observed = null;
                var summary = new ConductorBatchLoop().Run(
                    kernel, driver, ConductorAutonomyPolicy.Conservative, NoStopPath(),
                    maxIterations: 1, onTick: item => observed = item);
                Assert.Equal(1, summary.Held);
                Assert.Single(runs);
                Assert.Equal(GoalStatus.Verified, goal.Status);
                var retainedReceipt = Assert.IsType<ConductorParallelAcceptanceAttempt>(coordinator.TryReadLatest(goal.Id.Value));
                Assert.Equal(passedReceipt.AttemptId, retainedReceipt.AttemptId);
                Assert.Equal(passedReceipt.EffectiveManifestIdentity, retainedReceipt.EffectiveManifestIdentity);
                Assert.Null(retainedReceipt.RegateRequestedAt);
                Assert.Contains(observed!.ProgressLines!, line =>
                    line.Contains("result=held", StringComparison.Ordinal) &&
                    line.Contains($"Carry-forward_refused:_reason={expectedRefusal.Split(';')[0]}", StringComparison.Ordinal));
            }

            Assert.Single(Directory.GetFiles(Path.Combine(attemptRoot, goal.Id.Value), "*.attempt.json"));
            var notes = GoalOperationJournal.ReadActive(repository, goal.Id).Entries
                .Where(entry => entry.Operation == "conductor:criterion-evidence-refused-carry-hold")
                .ToArray();
            var note = Assert.Single(notes);
            Assert.Contains(obligation.Id, note.Detail, StringComparison.Ordinal);
            Assert.Contains(candidateSha, note.Detail, StringComparison.Ordinal);
            Assert.Contains($"criterion-evidence-map --goal {goal.Id.Value}", note.Detail,
                StringComparison.Ordinal);

            if (moveBranch)
            {
                AppendCommit(worktree, "src/goal.txt", "further goal changes");
                candidateSha = ReadGit(worktree, "rev-parse", "HEAD");
            }
            else if (repair != "exact-replay")
            {
                AppendCommit(repository, "src/main.txt", "new main commit");
                mainSha = ReadGit(repository, "rev-parse", "main");
            }

            AssertStillHeld(obligation);
            if (moveBranch)
            {
                driver.BeginTick(kernel, 0);
                var fallback = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);
                var held = Assert.IsType<ConductorAdvanceOutcome.Held>(fallback.Outcome);
                Assert.Equal("criterion-binding-refused-carry", held.Decision!.DiscriminatingEvidence);
                Assert.Single(runs);
            }

            if (repair == "new-brief")
            {
                kernel.ReviseGoalBrief(goal.Id, "Update src/goal.txt with the revised acceptance contract");
                kernel.RecordGoalRefinement(goal.Id, new RefinedSpec(
                    goal.Objective, ["Revised acceptance criterion"], VerificationClass.TestVerifiable, [], []));
                kernel.MapCriterionEvidenceOwner(goal.Id, 0, goal.AuthoritativeRefinedSpecVersion!.Version,
                    CriterionEvidenceOwner.Acceptance, "reviewer", CriterionEvidenceScopes.FullAcceptanceGate,
                    expectedCandidateSha: candidateSha);
            }
            else if (repair == "evidence-ready")
            {
                RecordEvidence(boundSha, "passed-old-branch", passed: true);
                Assert.Equal(boundSha, goal.CriterionEvidenceObligations.Single().ExpectedCandidateSha);
            }
            else
            {
                Rebind(0);
                if (repair == "partial")
                {
                    AssertStillHeld(goal.CriterionEvidenceObligations.Single(item => item.CriterionIndex == 1));
                    Rebind(1);
                }
                if (repair == "evidence")
                {
                    RecordEvidence(candidateSha, "verified-live-branch", passed: true);
                }
            }

            Tick();
            var fresh = Assert.IsType<ConductorParallelAcceptanceAttempt>(coordinator.TryReadLatest(goal.Id.Value));
            if (repair == "exact-replay")
            {
                Assert.Single(runs);
                Assert.Equal(passedReceipt.AttemptId, fresh.AttemptId);
                Assert.Contains(passedReceipt.AttemptId, goal.CriterionEvidenceObligations.Single().Detail, StringComparison.Ordinal);
                return;
            }

            Assert.Equal(2, runs.Count);
            Assert.Equal((candidateSha, mainSha), runs[1]);
            Assert.NotEqual(passedReceipt.AttemptId, fresh.AttemptId);
            Assert.Equal(candidateSha, fresh.BranchHeadSha);
            Assert.Equal(mainSha, fresh.MainHeadSha);

            void Rebind(int index) => kernel.MapCriterionEvidenceOwner(goal.Id, index, 1, CriterionEvidenceOwner.Acceptance,
                "operator rebind", CriterionEvidenceScopes.FullAcceptanceGate, expectedCandidateSha: candidateSha);

            void RecordEvidence(string sha, string receipt, bool passed) =>
                kernel.RecordCriterionEvidence(goal.Id, obligation.Id, CriterionEvidenceOwner.Acceptance,
                    sha, receipt, CriterionEvidenceScopes.FullAcceptanceGate, passed, "Deterministic verification evidence.");

            BatchTickSummary Tick()
            {
                BatchTickSummary? observed = null;
                new ConductorBatchLoop().Run(kernel, driver, ConductorAutonomyPolicy.Conservative,
                    NoStopPath(), maxIterations: 1, onTick: item => observed = item);
                return Assert.IsType<BatchTickSummary>(observed);
            }

            void AssertStillHeld(CriterionEvidenceObligation blocked)
            {
                var observed = Tick();
                Assert.Single(runs);
                Assert.Equal(passedReceipt.AttemptId, coordinator.TryReadLatest(goal.Id.Value)!.AttemptId);
                Assert.Contains(observed.ProgressLines!, line =>
                    line.Contains("result=held reason=criterion-binding-refused-carry", StringComparison.Ordinal) &&
                    line.Contains(candidateSha, StringComparison.Ordinal) &&
                    line.Contains(mainSha, StringComparison.Ordinal) &&
                    line.Contains(blocked.Id, StringComparison.Ordinal) &&
                    line.Contains("Operator_rebind", StringComparison.Ordinal));
            }
        }
        finally
        {
            TryDeleteDirectory(attemptRoot);
            TryDeleteDirectory(repository);
        }
    }
}
