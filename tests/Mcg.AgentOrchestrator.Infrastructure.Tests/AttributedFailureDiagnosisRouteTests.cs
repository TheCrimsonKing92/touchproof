using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using static AttributedFailureDeveloperRouteTests;
using static AttributedFailureDiagnosisCoordinatorTests;

// Parallel-safe: in-memory route harnesses and owned temporary artifacts; no real workers, gates or git.
public sealed class AttributedFailureDiagnosisRouteTests
{
    [Theory]
    [InlineData("failed")]
    [InlineData("timed-out")]
    [InlineData("no-cause")]
    [InlineData("cause-found")]
    public void CohortStillRetriesAndDispatchesDeveloperAfterDiagnosis(string status)
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = CompletedGoal(kernel);
        RecordCohortFailure(kernel, goal);
        var harness = new RouteHarness(kernel);
        var driver = harness.Configure();
        var calls = 0;
        driver.FailureDiagnosis = Coordinator(kernel, (_, route, _, _) =>
        {
            calls++;
            Assert.Empty(MarkerRetries(goal));
            Assert.Empty(harness.Dispatches);
            return Task.FromResult(new AttributedFailureDiagnosisOutcome(Candidate, route.Signature!, status, "diagnosed context"));
        });
        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive with { AutomaticRetryOperatorRoutingHoldSeconds = 0 });
        Assert.Equal(1, calls);
        var retry = Assert.Single(MarkerRetries(goal));
        Assert.Equal(goal.Tasks.Single().Id, Assert.Single(harness.Dispatches));
        Assert.Contains(status == "cause-found" ? "DIAGNOSIS (advice" : "DIAGNOSIS unavailable:", retry.Message);
        Assert.Empty(harness.Escalations);
    }

    [Fact]
    public void TrainUsesTheSameDiagnosisBeforeDeveloperDispatch()
    {
        using var fixture = new SoloCorrectionTestFixture();
        ApplyTrain(fixture.Kernel, fixture.Goal, fixture.Root, "ownership");
        var harness = new RouteHarness(fixture.Kernel);
        var driver = harness.Configure();
        driver.FailureDiagnosis = Coordinator(fixture.Kernel, (_, route, _, _) =>
            Task.FromResult(AttributedFailureDiagnosisPrompt.Parse(route, "DIAGNOSIS_STATUS: cause-found\ntrain cause at src/File.cs:1")));
        driver.AdvanceOnce(fixture.Goal, ConductorAutonomyPolicy.Permissive with { AutomaticRetryOperatorRoutingHoldSeconds = 0 });
        Assert.Single(harness.Dispatches);
        Assert.Contains("| train cause at src/File.cs:1", Assert.Single(MarkerRetries(fixture.Goal)).Message);
    }

    [Fact]
    public void SoloRecordsEnrichedFeedbackBeforeRetryAndSharedCoordinatorDoesNotDiagnoseTwice()
    {
        using var fixture = new SoloCorrectionTestFixture();
        fixture.Record();
        fixture.Trx("Tests.Fails");
        var calls = 0;
        var coordinator = Coordinator(fixture.Kernel, (_, route, _, _) =>
        {
            calls++;
            Assert.Empty(MarkerRetries(fixture.Goal));
            return Task.FromResult(AttributedFailureDiagnosisPrompt.Parse(route, "DIAGNOSIS_STATUS: cause-found\nsolo cause"));
        });
        string? charged = null;
        AttributedFailureRouteDecision? applied = null;
        var handler = new SoloAcceptanceFailureHandler(() => fixture.Kernel,
            (_, _, _, _, _, _) => throw new Exception("unexpected failure record"), () => DateTimeOffset.UnixEpoch,
            _ => fixture.Context,
            (goal, task, feedback) =>
            {
                charged = Assert.Single(feedback);
                Assert.Contains("| solo cause", charged);
                return fixture.Kernel.RecordCriterionRetryFeedback(goal, task, feedback);
            },
            (goal, prefix, policy, route) =>
            {
                applied = coordinator.Enrich(goal, route);
                fixture.Kernel.RetryTaskAutomatically(goal.Id, route.DeveloperTaskId!, applied.Feedback!, RetryCause.NewTestFinding);
                return new(goal.Id.Value, prefix, policy.Name, new ConductorAdvanceOutcome.Executed(GoalLifecycleState.AcceptanceFailed, applied.Feedback!));
            }, (_, _, _) => throw new Exception("unexpected hold"), _ => fixture.Attempt, coordinator.Enrich);
        handler.Route(fixture.Goal, fixture.Goal.Id.Value[..8], fixture.Policy);
        Assert.NotNull(applied);
        Assert.Equal(charged, applied.Feedback);
        Assert.Equal(1, calls);
        Assert.Single(MarkerRetries(fixture.Goal));
    }
}
