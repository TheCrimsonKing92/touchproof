using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using static AttributedFailureDeveloperRouteTests;
using static AttributedFailureDiagnosisCoordinatorTests;
using static AttributedFailureDiagnosisTargetTests;

// Parallel-safe: in-memory conductor, isolated artifact directories, and injected process callbacks.
public sealed class AttributedFailurePriorDiagnosisHoldTests
{
    private const string LegacyFinding = "**The candidate didn't cause these failures, and they don't show anything wrong with it.**";

    [Fact]
    public void SharedRouteHoldsBeforeRetryAndDispatch()
    {
        using var fixture = new SoloCorrectionTestFixture();
        var goal = fixture.Goal;
        var developer = goal.Tasks.Single();
        developer.ClearLastDispatch();
        RecordLegacy(goal, "first", string.Join(',', CohortTests), LegacyFinding);
        var latest = RecordLegacy(goal, "latest", string.Join(", ", CohortTests.Reverse().Append(CohortTests[0])), LegacyFinding);
        RecordCohortFailure(fixture.Kernel, goal);
        var harness = new RouteHarness(fixture.Kernel);
        var driver = harness.Configure();
        driver.FailureDiagnosis = MissingDispatch(fixture);
        var retries = MarkerRetries(goal);

        var result = driver.AdvanceOnce(goal, fixture.Policy with { AutomaticRetryOperatorRoutingHoldSeconds = 30 });

        AssertHeld(result, goal, latest, "latest", Assert.Single(harness.Escalations));
        Assert.DoesNotContain("candidate_sha=first", ((ConductorAdvanceOutcome.Held)result.Outcome).Reason);
        Assert.Equal(retries, MarkerRetries(goal));
        Assert.Empty(harness.Dispatches);
        AssertUnchanged(developer);
    }

    [Fact]
    public void SoloRouteHoldsBeforeChargingRetryOrForwarding()
    {
        using var fixture = new SoloCorrectionTestFixture();
        var goal = fixture.Goal;
        var developer = goal.Tasks.Single();
        developer.ClearLastDispatch();
        var previous = RecordLegacy(goal, "legacy", string.Join(',', CohortTests), LegacyFinding);
        fixture.Record();
        fixture.Trx(CohortTests.Reverse().ToArray());
        var retries = MarkerRetries(goal);
        var escalations = new List<string>();

        var result = RouteSolo(fixture, MissingDispatch(fixture), escalations,
            () => throw new InvalidOperationException("Retry feedback was charged"),
            () => throw new InvalidOperationException("Shared route was invoked"));

        AssertHeld(result, goal, previous, "legacy", Assert.Single(escalations));
        Assert.Equal(retries, MarkerRetries(goal));
        AssertUnchanged(developer);
    }

    [Theory]
    [InlineData(false, "newer-ambiguous")]
    [InlineData(true, "newer-ambiguous")]
    [InlineData(false, "partial")]
    [InlineData(true, "partial")]
    [InlineData(false, "absent")]
    [InlineData(true, "absent")]
    [InlineData(false, "other-unavailability")]
    [InlineData(true, "other-unavailability")]
    [InlineData(false, "newer-without-retry")]
    [InlineData(true, "newer-without-retry")]
    public void FallbackStillRetriesDeveloper(bool solo, string control)
    {
        using var fixture = new SoloCorrectionTestFixture();
        var goal = fixture.Goal;
        var developer = goal.Tasks.Single();
        developer.ClearLastDispatch();
        var identities = control switch
        {
            "partial" => CohortTests[0],
            "absent" => null,
            _ => string.Join(',', CohortTests)
        };
        RecordLegacy(goal, "older", identities, LegacyFinding);
        if (control == "newer-ambiguous")
            RecordLegacy(goal, "newer", identities, "The cause is uncertain.");
        if (control == "newer-without-retry")
            Coordinator(fixture.Kernel, (_, route, _, _) => Task.FromResult(
                AttributedFailureDiagnosisOutcome.Unavailable(route, "no-cause", "The cause is uncertain.")))
                .Enrich(goal, Route(goal, "newer", tests: CohortTests));
        var diagnosis = control == "other-unavailability"
            ? Coordinator(fixture.Kernel, (_, route, _, _) => Task.FromResult(
                AttributedFailureDiagnosisOutcome.Unavailable(route, "failed", "Reviewer inquiry agent unavailable")))
            : MissingDispatch(fixture);
        var before = MarkerRetries(goal).Length;

        if (solo)
        {
            fixture.Record();
            fixture.Trx(CohortTests);
            var holds = new List<string>();
            var forwards = 0;
            var result = RouteSolo(fixture, diagnosis, holds, () => { }, () => forwards++);
            Assert.True(result.WasExecuted);
            Assert.Empty(holds);
            Assert.Equal(1, forwards);
            Assert.Equal(1, developer.CriterionRetryCount);
        }
        else
        {
            RecordCohortFailure(fixture.Kernel, goal);
            var harness = new RouteHarness(fixture.Kernel);
            var driver = harness.Configure();
            driver.FailureDiagnosis = diagnosis;
            driver.AdvanceOnce(goal, fixture.Policy with { AutomaticRetryOperatorRoutingHoldSeconds = 0 });
            Assert.Empty(harness.Escalations);
            Assert.Equal(developer.Id, Assert.Single(harness.Dispatches));
        }

        Assert.Equal(before + 1, MarkerRetries(goal).Length);
        Assert.NotEqual(WorkTaskStatus.Completed, developer.Status);
    }

    private static AttributedFailureDiagnosisCoordinator MissingDispatch(SoloCorrectionTestFixture fixture) =>
        Coordinator(fixture.Kernel, Inquiry(fixture.Root, (_, _) => throw new InvalidOperationException("No process expected")).RunAsync);

    private static ProgressEvent RecordLegacy(Goal goal, string revision, string? failingTests, string finding)
    {
        var task = goal.Tasks.Single().Id;
        var signature = "signature-" + revision;
        var header = $"candidate_sha={revision} signature={signature}";
        var note = new ProgressEvent(goal.Id, task, ProgressKind.TaskNote,
            $"ATTRIBUTED_FAILURE_DIAGNOSIS {header} status=cause-found\r\n| {finding} Additional diagnosis detail.",
            DateTimeOffset.UnixEpoch);
        goal.Append(note);
        if (failingTests is not null)
            goal.Append(new(goal.Id, task, ProgressKind.TaskRetried,
                $"ATTRIBUTED_FAILURE_ROUTE source=train-recorded candidate_sha={revision} receipt_id=receipt-{revision} signature={signature}" +
                $"\r\nfailing_tests={failingTests}\r\nDIAGNOSIS (advice; the Developer must decide whether the code or test is wrong):\r\n| {finding}",
                DateTimeOffset.UnixEpoch.AddSeconds(1)));
        return note;
    }

    private static ConductorAdvanceResult RouteSolo(SoloCorrectionTestFixture fixture, AttributedFailureDiagnosisCoordinator diagnose,
        List<string> escalations, Action charge, Action forward)
    {
        var handler = new SoloAcceptanceFailureHandler(() => fixture.Kernel,
            (_, _, _, _, _, _) => throw new InvalidOperationException("Unexpected acceptance failure record"),
            () => DateTimeOffset.UnixEpoch, _ => fixture.Context,
            (goal, task, feedback) =>
            {
                charge();
                return fixture.Kernel.RecordCriterionRetryFeedback(goal, task, feedback);
            },
            (goal, prefix, policy, route) =>
            {
                forward();
                fixture.Kernel.RetryTaskAutomatically(goal.Id, route.DeveloperTaskId!, route.Feedback!, RetryCause.NewTestFinding);
                return new(goal.Id.Value, prefix, policy.Name,
                    new ConductorAdvanceOutcome.Executed(GoalLifecycleState.AcceptanceFailed, route.Feedback!));
            },
            (_, state, reason) =>
            {
                Assert.Equal(GoalLifecycleState.AcceptanceFailed, state);
                escalations.Add(reason);
            }, _ => fixture.Attempt, diagnose.Enrich);
        return handler.Route(fixture.Goal, fixture.Goal.Id.Value[..8], fixture.Policy);
    }

    private static void AssertHeld(ConductorAdvanceResult result, Goal goal, ProgressEvent prior, string revision, string escalation)
    {
        var held = Assert.IsType<ConductorAdvanceOutcome.Held>(result.Outcome);
        Assert.Equal(ConductorHoldOwner.Operator, held.Owner);
        Assert.Equal(GoalLifecycleState.AcceptanceFailed, held.State);
        Assert.Equal(GoalStatus.AcceptanceFailed, goal.Status);
        Assert.Equal(held.Reason, escalation);
        Assert.Contains($"task={prior.TaskId!.Value} occurred_at={prior.OccurredAt:O}", held.Reason);
        Assert.Contains($"candidate_sha={revision} signature=signature-{revision} receipt_id=receipt-{revision}", held.Reason);
        Assert.Contains("found the candidate not at fault", held.Reason);
        Assert.Contains(LegacyFinding, held.Reason);
        Assert.Contains(Assert.IsType<PolicyDecisionRecord>(held.Decision).Facts,
            fact => fact is { Name: "prior_finding", Value: LegacyFinding });
    }

    private static void AssertUnchanged(TaskSpec developer)
    {
        Assert.Equal(WorkTaskStatus.Completed, developer.Status);
        Assert.Equal(0, developer.CriterionRetryCount);
        Assert.Null(developer.LatestRetryAt);
        Assert.Null(developer.LastDispatch);
        Assert.Null(developer.AcceptanceRetryRoutingHold);
        Assert.Single(developer.DispatchHistory);
    }
}
