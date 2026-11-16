using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using static AttributedFailureDeveloperRouteTests;

// Parallel-safe: each row owns its in-memory kernel and stub dispatches.
public sealed class AttributedTrainFailureCurrencyTests
{
    [Theory(Timeout = TestHangGuard.WholeTestMilliseconds)]
    [Trait("Category", "CrossTick")]
    [InlineData("ownership")]
    [InlineData("reach")]
    [InlineData("path")]
    [InlineData("recorded")]
    [InlineData("reproduction")]
    [InlineData("bisection")]
    [InlineData("survivors-passed")]
    public void CurrentMember_AdvancedMain_AdmitsFreshGateWithoutRetry(string source)
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = CompletedGoal(kernel);
        RecordCohortFailure(kernel, goal, evidence: Evidence(source));
        var failure = goal.LatestAcceptanceFailure!;
        Assert.Equal(Main, failure.MainHeadSha);
        Assert.NotNull(AttributedFailureDeveloperRoute.ReadAcceptanceFailure(failure));
        var retries = goal.AutomaticAcceptanceRetryCount;
        var harness = new RouteHarness(kernel);
        var driver = harness.Configure();
        driver.CohortAttributionRevisionReader = _ => new(Candidate, "advanced-main");
        driver.BeginTick(kernel, 1);

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive with
        {
            AutomaticRetryOperatorRoutingHoldSeconds = 0
        });

        Assert.False(result.WasEscalated);
        Assert.Empty(harness.Escalations);
        var admitted = Assert.IsType<ConductorAdvanceOutcome.Executed>(result.Outcome);
        Assert.Equal("FreshGate", admitted.Decision?.Action);
        Assert.Contains(new PolicyDecisionFact("candidateSha", Candidate), admitted.Decision!.Facts);
        Assert.Contains(new PolicyDecisionFact("mainSha", "advanced-main"), admitted.Decision.Facts);
        Assert.Equal(GoalStatus.Verified, goal.Status);
        Assert.Equal(WorkTaskStatus.Completed, goal.Tasks.Single().Status);
        Assert.Null(goal.LatestAcceptanceFailure);
        Assert.Empty(harness.Dispatches);
        Assert.Empty(MarkerRetries(goal));
        Assert.Equal(retries, goal.AutomaticAcceptanceRetryCount);
        Assert.Contains(goal.Timeline, evt => evt.Message.Contains(
            $"recorded branch={Candidate} main={Main}; current branch={Candidate} main=advanced-main.", StringComparison.Ordinal));
        driver.BeginTick(kernel, 2);
        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);
        Assert.Single(goal.Timeline.Where(evt => evt.Message.StartsWith("SOLO_FRESH_GATE ", StringComparison.Ordinal)));
        Assert.Empty(harness.Dispatches);
        Assert.Empty(MarkerRetries(goal));
        Assert.Equal(retries, goal.AutomaticAcceptanceRetryCount);
    }

    [Theory]
    [InlineData("ownership", "new-candidate", "advanced-main", "Superseded")]
    [InlineData("ownership", Candidate, null, "Unknown")]
    [InlineData("ownership", null, "advanced-main", "Unknown")]
    [InlineData("ownership", Candidate, " ", "Unknown")]
    [InlineData("malformed", Candidate, "advanced-main", "Superseded")]
    [InlineData("cohort", Candidate, "advanced-main", "Superseded")]
    [InlineData("solo", Candidate, "advanced-main", "Superseded")]
    public void UnboundOrDifferentEvidence_DoesNotAuthorizeTrainRepair(string source,
        string? currentCandidate, string? currentMain, string expected)
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = CompletedGoal(kernel);
        RecordCohortFailure(kernel, goal, evidence: Evidence(source));

        var currency = AttributedFailureDeveloperRoute.ClassifyAcceptanceFailure(
            goal.LatestAcceptanceFailure!, currentCandidate, currentMain);

        Assert.Equal(expected, currency.ToString());
        var route = AttributedFailureDeveloperRoute.Decide(goal,
            AttributedFailureDeveloperRoute.ReadAcceptanceFailure(goal.LatestAcceptanceFailure),
            currentCandidate, currentMain ?? " ");
        Assert.False(route.IsRoute);
    }

    private static string Evidence(string source) => source switch
    {
        "survivors-passed" => "merge train RED attributed (survivors-passed) receipt train-red: Tests.Fails\nsurvivors_passed Tests.Fails: red",
        "reproduction" or "bisection" => $"merge train RED attributed ({source}) receipt train-red: Tests.Fails\nreproduced Tests.Fails: red",
        "cohort" => "cohort-attribution=cohort-red cohort-partition=partition-red reproduced=Tests.Fails",
        "solo" => "solo failure",
        "malformed" => "merge train RED attributed (ownership) receipt train-red: Tests.Fails\nmalformed detail",
        _ => $"merge train RED attributed ({source}) receipt train-red: Tests.Fails"
    };
}
