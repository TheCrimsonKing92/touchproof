using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using Fixture = MergeTrainSurvivorDifferentialFixture;

// Parallel-safe: the fixture owns each repository, worktree, kernel and store.
public sealed class MergeTrainSurvivorDifferentialRouteTests(CohortCandidateTemplate template)
    : IClassFixture<CohortCandidateTemplate>
{
    [Theory]
    [InlineData("fatal")]
    [InlineData("theory")]
    public void PassingSurvivors_RouteDeveloperWithBoundedDifferentialEvidence(string mode)
    {
        new Fixture(template).WithTrain(scenario =>
        {
            var dropped = Assert.Single(scenario.Result.Ejections);
            Assert.Equal(MergeTrainEjectionReason.RedNewestMember, dropped.Reason);
            Assert.Equal(scenario.Dropped.Id, dropped.GoalId);
            var survivors = Assert.IsType<MergeTrainReceipt>(scenario.Result.Receipt);
            Assert.Equal(MergeTrainGateOutcome.Passed, survivors.Outcome);
            Assert.Equal(scenario.Goals.Take(2).Select(goal => goal.Id), survivors.Identity.Members.Select(member => member.GoalId));
            Assert.Equal(2, scenario.Runs());
            Assert.Equal(GoalStatus.AcceptanceFailed, scenario.Dropped.Status);
            Assert.Equal(scenario.Selection.Members[2].CandidateRevision, scenario.Dropped.LatestAcceptanceFailure!.BranchHeadSha);
            Assert.Single(scenario.Routes());
            var text = scenario.Advance(scenario.Dropped, "train-survivors-passed");
            foreach (var failure in MergeTrainRedAttribution.ReadFatalFailures(scenario.Red.GateTestResultPaths))
            {
                Assert.Contains(failure.TestName!, text);
                Assert.Contains("failed " + failure.TestName + ": " + MergeTrainAttributedFailure.BoundMessage(Fixture.Message), text);
            }
            Assert.Contains(scenario.Red.ReceiptId, text);
            Assert.Contains(survivors.ReceiptId, text);
            Assert.All(scenario.Red.GateTestResultPaths, path => Assert.Contains("red_trx=" + path, text));
            Assert.Contains("with this member present", text);
            Assert.Contains("only this member was absent", text);
            Assert.DoesNotContain("UNBOUNDED_TAIL", text);
            Assert.DoesNotContain("reproduc", text, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(RetryCause.NewTestFinding, scenario.Dropped.Tasks.Single().PendingRetryCause);
        }, mode);
    }

    [Fact]
    public void PartialRedEvidence_UsesFatalResultsWhenClassReaderDeclines()
    {
        new Fixture(template).WithTrain(scenario =>
        {
            Assert.Equal(GoalStatus.Verified, scenario.Dropped.Status);
            var red = scenario.Red with { GateTestResultPaths = [.. scenario.Red.GateTestResultPaths, Path.Combine(scenario.Repo, "missing.trx")] };
            Assert.True(MergeTrainRedAttribution.IsGenuineTrainRed(red));
            Assert.Null(MergeTrainReachAttribution.ReadFailingClasses(red.GateTestResultPaths));
            MergeTrainSurvivorDifferentialFailure.Route(red, scenario.Result.Receipt!, red.Identity.Members[2],
                scenario.Result.Ejections, scenario.Dropped,
                goal => scenario.Driver.ProjectGateReadyCandidate(goal, ConductorAutonomyPolicy.Permissive),
                (goal, checks, branch, main, attributions, baseline) =>
                    scenario.Kernel.RecordAcceptanceFailure(goal.Id, checks, branch, main, attributions, baseline),
                scenario.Kernel.RouteRecordedAcceptanceFailure);
            Assert.Equal(GoalStatus.AcceptanceFailed, scenario.Dropped.Status);
            var text = scenario.Advance(scenario.Dropped, "train-survivors-passed");
            Assert.Contains(Fixture.Test, text);
            Assert.All(red.GateTestResultPaths, path => Assert.Contains("red_trx=" + path, text));
            Assert.Contains(red.ReceiptId, text);
            Assert.Contains(scenario.Result.Receipt!.ReceiptId, text);
        }, readOnlyChild: true);
    }

    [Fact]
    public void ReplayAndRestart_RecordAndRetryOnlyOnce()
    {
        new Fixture(template).WithTrain(scenario =>
        {
            var failure = scenario.Dropped.LatestAcceptanceFailure;
            Assert.NotNull(failure);
            scenario.Replay();
            Assert.Empty(scenario.Reconcile());
            Assert.Empty(scenario.Reconcile());
            Assert.Same(failure, scenario.Dropped.LatestAcceptanceFailure);
            Assert.Single(scenario.Routes());
            Assert.Equal(2, scenario.Runs());
            scenario.Advance(scenario.Dropped, "train-survivors-passed");
            var restartedDriver = scenario.Restart(out var restarted);
            Assert.Empty(restartedDriver.ReconcileAttributedTrainFailures(ConductorAutonomyPolicy.Permissive));
            var goal = restarted.GetGoal(scenario.Dropped.Id);
            Assert.Single(scenario.Routes(goal));
            Assert.Single(AttributedFailureDeveloperRouteTests.MarkerRetries(goal));
            Assert.Single(goal.Timeline.Where(record => record.Kind == ProgressKind.TaskRetried));
        });
    }

    [Fact(Timeout = TestHangGuard.WholeTestMilliseconds)]
    [Trait("Category", "CrossTick")]
    public void NextThreeTicks_ExcludeFailedCandidateWithoutAnotherGate()
    {
        new Fixture(template).WithTrain(scenario =>
        {
            Assert.Equal(GoalStatus.AcceptanceFailed, scenario.Dropped.Status);
            var harness = new AttributedFailureDeveloperRouteTests.RouteHarness(scenario.Kernel,
                scenario.Selection.Members[2].CandidateRevision);
            var routing = harness.Configure();
            var attemptFiles = Files(scenario.Workspace.OrchestratorDirectory);
            for (var tick = 1; tick <= 3; tick++)
            {
                scenario.Driver.BeginTick(scenario.Kernel, tick);
                routing.BeginTick(scenario.Kernel, tick);
                Assert.Empty(scenario.Reconcile());
                var candidates = scenario.Goals.Select(goal => new ConductorSpeculativeAcceptanceCandidate(goal.Id,
                    scenario.Driver.ProjectGateReadyCandidate(goal, ConductorAutonomyPolicy.Permissive))).ToArray();
                Assert.IsNotType<GateReadyCandidateProjectionResult.Ready>(candidates[2].ProjectionResult);
                Assert.DoesNotContain(ConductorMergeTrainSelector.Select(candidates)?.Members ?? [], member => member.GoalId == scenario.Dropped.Id);
                Assert.DoesNotContain(ConductorAcceptanceCohortSelector.Select(candidates).Selection?.Members ?? [], member => member.GoalId == scenario.Dropped.Id);
                routing.AdvanceOnce(scenario.Dropped, ConductorAutonomyPolicy.Permissive);
                Assert.Equal(2, scenario.Runs());
            }
            Assert.Empty(harness.Escalations);
            Assert.Single(harness.Dispatches);
            Assert.Single(AttributedFailureDeveloperRouteTests.MarkerRetries(scenario.Dropped));
            Assert.Single(scenario.Dropped.Timeline.Where(record => record.Kind == ProgressKind.TaskRetried));
            Assert.Equal(attemptFiles, Files(scenario.Workspace.OrchestratorDirectory));
        });
    }

    [Fact]
    public void ReadOnlyChild_ParentRecoversAndCheckpointsExactlyOneRoute()
    {
        new Fixture(template).WithTrain(scenario =>
        {
            Assert.Equal(GoalStatus.Verified, scenario.Dropped.Status);
            Assert.Equal(GoalStatus.AcceptanceFailed, scenario.ExecutingKernel.GetGoal(scenario.Dropped.Id).Status);
            Assert.All(scenario.Repository.LoadAsync().GetAwaiter().GetResult().Goals,
                goal => Assert.Equal(GoalStatus.Verified, goal.Status));
            var changed = new HashSet<GoalId>();
            var remaining = MergeTrainAttributedFailureReconciliation.PrepareAdmission(scenario.Driver,
                scenario.Goals, ConductorAutonomyPolicy.Permissive, changed);
            Assert.Equal(scenario.Dropped.Id, Assert.Single(changed));
            Assert.Equal(scenario.Goals.Take(2), remaining);
            Assert.Empty(scenario.Reconcile());
            scenario.Repository.SaveGoalSnapshotsAsync(scenario.Kernel.ExportSnapshot().Goals
                .Where(goal => changed.Contains(new GoalId(goal.Id))).ToArray()).GetAwaiter().GetResult();
            var persisted = scenario.Repository.LoadAsync().GetAwaiter().GetResult().GetGoal(scenario.Dropped.Id);
            Assert.Equal(GoalStatus.AcceptanceFailed, persisted.Status);
            Assert.Single(scenario.Routes(persisted));
            scenario.Advance(scenario.Dropped, "train-survivors-passed");
            var driver = scenario.Restart(out var restarted);
            Assert.Empty(driver.ReconcileAttributedTrainFailures(ConductorAutonomyPolicy.Permissive));
            Assert.Single(scenario.Routes(restarted.GetGoal(scenario.Dropped.Id)));
            Assert.Single(AttributedFailureDeveloperRouteTests.MarkerRetries(restarted.GetGoal(scenario.Dropped.Id)));
            Assert.Single(restarted.GetGoal(scenario.Dropped.Id).Timeline.Where(record => record.Kind == ProgressKind.TaskRetried));
        }, readOnlyChild: true);
    }

    private static string[] Files(string root) => Directory.GetFiles(root, "*attempt*.json", SearchOption.AllDirectories)
        .Order(StringComparer.Ordinal).ToArray();
}
