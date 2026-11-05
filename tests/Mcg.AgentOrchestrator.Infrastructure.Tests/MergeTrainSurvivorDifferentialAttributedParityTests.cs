using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Fixture = MergeTrainSurvivorDifferentialFixture;

// Parallel-safe: this scenario owns its repository, worktrees, kernels and database.
public sealed class MergeTrainSurvivorDifferentialAttributedParityTests(CohortCandidateTemplate template)
    : IClassFixture<CohortCandidateTemplate>
{
    [Fact]
    public void AttributedMember_KeepsExistingApplyRouteOncePerReceipt()
    {
        new Fixture(template).WithTrain(scenario =>
        {
            var ejection = Assert.Single(scenario.Result.Ejections);
            Assert.Equal(scenario.Goals[0].Id, ejection.GoalId);
            Assert.Equal(MergeTrainEjectionReason.RedAttributedMember, ejection.Reason);
            Assert.Equal(MergeTrainGateOutcome.Passed, scenario.Result.Receipt!.Outcome);
            Assert.Equal(GoalStatus.AcceptanceFailed, scenario.Goals[0].Status);
            Assert.All(scenario.Goals.Skip(1), goal => Assert.Equal(GoalStatus.Verified, goal.Status));
            Assert.Equal(new[] { "AlphaReachTests", "ZebraReachTests" }, scenario.Goals[0].LatestAcceptanceFailure!.FailedChecks);
            Assert.Contains("merge train RED attributed (ownership)", Assert.Single(scenario.Routes(scenario.Goals[0])).Message);
            var records = 0;
            var routes = 0;
            MergeTrainAttributedFailure.Apply(scenario.Red, scenario.Red.Identity.Members[0], scenario.Goals[0],
                "ownership", 3, [], _ => throw new InvalidOperationException("Unexpected ejection"),
                (_, _, _, _, _, _) => records++, (_, _) => { routes++; return true; });
            Assert.Equal(0, records);
            Assert.Equal(0, routes);
            scenario.Replay();
            Assert.Empty(scenario.Reconcile());
            Assert.Empty(scenario.Reconcile());
            var text = scenario.Advance(scenario.Goals[0], "train-ownership");
            Assert.Contains("AlphaReachTests", text);
            Assert.Contains("ZebraReachTests", text);
            Assert.Contains(scenario.Red.ReceiptId, text);
            Assert.All(scenario.Goals, goal => Assert.DoesNotContain(goal.Timeline,
                record => record.Message.Contains("survivors-passed", StringComparison.Ordinal)));
            Assert.Single(scenario.Routes(scenario.Goals[0]));
            Assert.Single(AttributedFailureDeveloperRouteTests.MarkerRetries(scenario.Goals[0]));
            Assert.Equal(2, scenario.Runs());
        }, "attributed");
    }
}
