using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Fixture = MergeTrainSurvivorDifferentialFixture;

// Parallel-safe: every row owns all its repositories, worktrees and SQLite stores.
public sealed class MergeTrainSurvivorDifferentialNegativeControlTests(CohortCandidateTemplate template)
    : IClassFixture<CohortCandidateTemplate>
{
    [Theory]
    [InlineData("apparatus", 2, 1, MergeTrainGateOutcome.Passed)]
    [InlineData("unreadable", 1, 0, MergeTrainGateOutcome.InfrastructureFailure)]
    [InlineData("survivors-red", 2, 1, MergeTrainGateOutcome.Failed)]
    [InlineData("guard", 2, 1, MergeTrainGateOutcome.Passed)]
    public void NonQualifyingEvidence_PreservesDispositionAndNeverRoutes(
        string mode, int runs, int ejections, MergeTrainGateOutcome lastOutcome)
    {
        new Fixture(template).WithTrain(scenario =>
        {
            Assert.Equal(runs, scenario.Runs());
            Assert.Equal(ejections, scenario.Result.Ejections.Count);
            Assert.All(scenario.Result.Ejections, ejection =>
            {
                Assert.Equal(scenario.Dropped.Id, ejection.GoalId);
                Assert.Equal(MergeTrainEjectionReason.RedNewestMember, ejection.Reason);
            });
            Assert.Equal(lastOutcome, (scenario.Result.Receipt ?? scenario.Result.RecordedReceipt)!.Outcome);
            Assert.Empty(scenario.Driver.ReadTrainImplicatedMemberKeys());
            Assert.Equal(mode == "survivors-red" ? 2 : 0, scenario.Driver.ReadSuppressedGroupedPairs().Count);
            Assert.Empty(scenario.Reconcile());
            Assert.Empty(scenario.Reconcile());
            Assert.All(scenario.Goals, goal =>
            {
                Assert.Equal(GoalStatus.Verified, goal.Status);
                Assert.Null(goal.LatestAcceptanceFailure);
                Assert.Empty(scenario.Routes(goal));
                Assert.Empty(AttributedFailureDeveloperRouteTests.MarkerRetries(goal));
            });
            var restarted = scenario.Restart(out var kernel);
            Assert.Empty(restarted.ReconcileAttributedTrainFailures(ConductorAutonomyPolicy.Permissive));
            Assert.All(kernel.Goals, goal => Assert.Equal(GoalStatus.Verified, goal.Status));
            Assert.Equal(runs, scenario.Runs());
        }, mode);
    }

    [Fact]
    public void AdvancedCandidate_DoesNotInheritHistoricalDifferentialFailure()
    {
        new Fixture(template).WithTrain(scenario =>
        {
            Assert.Equal(GoalStatus.Verified, scenario.Dropped.Status);
            Assert.Equal(GoalStatus.AcceptanceFailed, scenario.ExecutingKernel.GetGoal(scenario.Dropped.Id).Status);
            var revised = scenario.ReviseDropped();
            var projection = Assert.IsType<GateReadyCandidateProjectionResult.Ready>(
                scenario.Driver.ProjectGateReadyCandidate(scenario.Dropped, ConductorAutonomyPolicy.Permissive));
            Assert.Equal(revised, projection.Projection.CandidateRevision);
            Assert.NotEqual(scenario.Selection.Members[2].CandidateRevision, revised);
            Assert.Empty(scenario.Reconcile());
            Assert.Empty(scenario.Reconcile());
            Assert.Equal(GoalStatus.Verified, scenario.Dropped.Status);
            Assert.Null(scenario.Dropped.LatestAcceptanceFailure);
            Assert.Empty(scenario.Routes());
            Assert.Empty(AttributedFailureDeveloperRouteTests.MarkerRetries(scenario.Dropped));
            var restarted = scenario.Restart(out var kernel);
            Assert.Empty(restarted.ReconcileAttributedTrainFailures(ConductorAutonomyPolicy.Permissive));
            Assert.Equal(GoalStatus.Verified, kernel.GetGoal(scenario.Dropped.Id).Status);
        }, readOnlyChild: true);
    }
}
