using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: repository, SQLite stores, results and build storage are fixture-local;
// the substituted gate publishes evidence without launching external verification.
public sealed class SelectedExecutionBackstopTrainLandingIntakeTests : AcceptanceCohortWorkflowTests
{
    [Fact(Timeout = TestHangGuard.WholeTestMilliseconds)]
    public async Task ProductionTrain_TwoLandingsRetainAppliedGateRecordAndSkippedClasses()
    {
        var repo = CreateReducedAcceptanceCohortRepository();
        try
        {
            AddAcceptanceManifest(repo);
            AddSourceSizeAuthority(repo, ("tests/Mcg.AgentOrchestrator.Core.Tests/TrainFirst.cs", 1));
            var cleanup = CreateIsolatedCleanupContext(repo);
            var kernel = new AgentOrchestratorKernel();
            var first = CreateCompletedGoal(kernel, "First selected train member", repo);
            var second = CreateCompletedGoal(kernel, "Second selected train member", repo);
            foreach (var goal in new[] { first, second })
                kernel.RecordGoalRefinement(goal.Id, new RefinedSpec(goal.Objective,
                    ["The full acceptance gate passes"], VerificationClass.TestVerifiable, [], [])
                { AcceptanceGateOwnedAcceptanceCriteria = ["The full acceptance gate passes"] });
            CreateWorktreeCandidate(repo, first.Id, "tests/Mcg.AgentOrchestrator.Core.Tests/TrainFirst.cs", "first");
            CreateWorktreeCandidate(repo, second.Id, "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/TrainSecond.cs", "second");
            using var backstop = new SelectedExecutionBackstopFixture();
            var verifier = new PublishingVerifier();
            var driver = new ConductorDriver(kernel, OrchestratorWorkspace.ForDirectory(repo), verifier,
                AgentCatalog.Default().Agents, WorkerProfileCatalog.Default(), cleanupHooks: cleanup.Hooks);
            driver.SuccessfulLandingSink = receipt => backstop.Coordinator.NoteLanding(receipt, repo);

            var result = driver.RunMergeTrain(ProjectTrainSelection(driver, first, second),
                [first, second], ConductorAutonomyPolicy.Permissive);

            Assert.Equal(1, verifier.RunCount);
            Assert.Equal(MergeTrainGateOutcome.Passed, result.Receipt?.Outcome);
            Assert.Equal(2, result.MemberResults.Count);
            var state = Assert.IsType<SelectedExecutionBackstopState>(await backstop.Store.ReadAsync());
            Assert.Equal(new[] { first.Id.Value, second.Id.Value }.Order(), state.RangeLandings.Select(landing => landing.GoalId).Order());
            Assert.Equal(2, state.Uncovered.Count);
            var expectedSkipped = verifier.PublishedPlan!.SelectedExecution!.Lanes
                .SelectMany(lane => lane.SkippedClasses).Distinct(StringComparer.Ordinal).ToArray();
            Assert.NotEmpty(expectedSkipped);
            Assert.All(state.RangeLandings, landing =>
            {
                Assert.True(landing.Applied);
                Assert.False(landing.RecordUnreadable);
                Assert.Contains(landing, state.Uncovered);
                Assert.Equal(expectedSkipped, landing.SkippedClasses);
            });
            Assert.DoesNotContain(backstop.Lines, line => line.Contains("landing-record-unreadable"));
            AssertNoMergeTrainWorkspaces(repo);
        }
        finally { DeleteDirectory(repo); }
    }

    private sealed class PublishingVerifier : IGoalAcceptanceVerifier
    {
        internal int RunCount { get; private set; }
        internal AcceptanceGateLanePlan? PublishedPlan { get; private set; }

        public Task<AcceptanceVerificationResult> RunOwnedAsync(string worktreePath, GoalId? goalId,
            IReadOnlyList<string>? changedFiles, int? stableSlotIndex, DotnetBuildEnvironmentLease? stableSlotLease,
            IAcceptanceAttemptExecutionOwner executionOwner)
        {
            RunCount++;
            var owner = Assert.IsType<AcceptanceAttemptExecutionOwner>(executionOwner);
            PublishedPlan = new(AcceptanceGateLanePlan.CurrentSchemaVersion, owner.RunId, [], [])
            { SelectedExecution = SelectedExecutionBackstopFixture.Applied };
            AcceptanceGateLanePlanPublisher.Publish(PublishedPlan, owner.ResultsPrefix, goalId,
                owner.ReportProgress, TimeProvider.System);
            Assert.True(File.Exists(owner.ResultsPrefix + AcceptanceGateLanePlan.ArtifactSuffix));
            return Task.FromResult(new AcceptanceVerificationResult(true, false, 0, null,
                Checks: [new AcceptanceCheckResult("merge train", true, 0, null)],
                TestResultPaths: [WritePassingTrx(Path.GetDirectoryName(owner.ResultsPrefix)!, "selected-train-green.trx")]));
        }

        public Task<FocusedEvidenceRunResult> RunFocusedEvidenceOwnedAsync(string worktreePath, GoalId? goalId,
            string request, IAcceptanceFocusedVerificationOwner executionOwner, int? stableSlotIndex = null,
            DotnetBuildEnvironmentLease? stableSlotLease = null, bool runBaselineArm = false) =>
            throw new NotSupportedException("This train must use its ordinary gate attempt.");
    }
}
