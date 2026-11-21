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
    public void ReviewedLegacyDiagnosisWithEvidenceCanStillHold()
    {
        using var fixture = new SoloCorrectionTestFixture();
        var goal = fixture.Goal;
        goal.Tasks.Single().ClearLastDispatch();
        const string candidate = "b0bb1978aeb4993dba5244447134427037dfa796";
        const string signature = "88d99d42a3359988";
        const string receipt = "merge-train-receipt-8a083f0275210853c2f4a2e82d1d4130000647d91326d2bb334946fd81f29d0e";
        string[] tests = ["AcceptanceCohortWorkflowTestsLongCohortRepeatedRuns", "OrchestrationCliCommandViewArchitectureTests"];
        var prior = RecordLegacy(goal, candidate, string.Join(',', tests), ReviewedLegacyAdvice, signature, receipt);

        var route = MissingDispatch(fixture).Enrich(goal, Route(goal, tests: tests));
        var result = AttributedFailureDiagnosisTarget.TryHold(goal, goal.Id.Value[..8], fixture.Policy, route, (_, _, _) => { });

        var held = Assert.IsType<ConductorAdvanceOutcome.Held>(result?.Outcome);
        Assert.Equal(ConductorHoldOwner.Operator, held.Owner);
        Assert.Contains($"task={prior.TaskId!.Value} occurred_at={prior.OccurredAt:O}", held.Reason);
        Assert.Contains($"candidate_sha={candidate} signature={signature} receipt_id={receipt}", held.Reason);
        Assert.Contains(LegacyFinding, held.Reason);
    }

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
    [InlineData(false, "same-line-retraction")]
    [InlineData(true, "same-line-retraction")]
    [InlineData(false, "later-line-retraction")]
    [InlineData(true, "later-line-retraction")]
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
        var newerFinding = control switch
        {
            "newer-ambiguous" => "The cause is uncertain.",
            "same-line-retraction" => "The candidate did not cause these failures. That was my initial assessment. The candidate diff confirms it introduced the failures.",
            "later-line-retraction" => LegacyFinding + "\nThe candidate diff confirms it introduced the failures.",
            _ => null
        };
        if (newerFinding is not null)
            RecordLegacy(goal, "newer", identities, newerFinding);
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

    private static ProgressEvent RecordLegacy(Goal goal, string revision, string? failingTests, string finding,
        string? signature = null, string? receipt = null)
    {
        var task = goal.Tasks.Single().Id;
        signature ??= "signature-" + revision;
        receipt ??= "receipt-" + revision;
        var note = new ProgressEvent(goal.Id, task, ProgressKind.TaskNote,
            AttributedFailureDiagnosisRecord.Format(new(revision, signature, "cause-found", finding)),
            DateTimeOffset.UnixEpoch);
        goal.Append(note);
        if (failingTests is not null)
            goal.Append(new(goal.Id, task, ProgressKind.TaskRetried,
                $"ATTRIBUTED_FAILURE_ROUTE source=train-recorded candidate_sha={revision} receipt_id={receipt} signature={signature}" +
                $"\r\nfailing_tests={failingTests}\r\nDIAGNOSIS (advice; the Developer must decide whether the code or test is wrong):\r\n| {finding}",
                DateTimeOffset.UnixEpoch.AddSeconds(1)));
        return note;
    }

    // Verbatim advice from goal cfda6f88, event 149, supplied as attested legacy evidence.
    private const string ReviewedLegacyAdvice = """
        **The candidate didn't cause these failures, and they don't show anything wrong with it.** None of the failing tests touch Refined Spec brief rendering. The gate ran in train worktree `t-b0464a8b8ec4`, and the failures come from code another goal brought into that train.

        **1. `OrchestrationCliCommandViewArchitectureTests` (both tests): the code comes from goal 1deac5c0**
        - The forbidden dependency is `Mcg.AgentOrchestrator.App.Orchestration.WorkerUsageSampleSource` → `App.Cli.ContextUsageFigure`, `ContextUsageFigures` and `ContextUsageReport`.
        - `WorkerUsageSampleSource` does not exist in this candidate. A search of `src/Mcg.AgentOrchestrator.App/Orchestration` at b0bb1978a finds only the allowed `BoardFillCliGoalIntake.cs:76` reference.
        - `git log --all -S WorkerUsageSampleSource` finds the type only in commits `5bcb7558b` and `957d49a4c`, both titled "Developer(1deac5c0): Give the worker usage outcome metrics a real sample". Neither commit is on any branch in this worktree, which is what you'd expect from another goal's train candidate.
        - The `BoardFillCliGoalIntake` message in `Empty_exemptions_Control_names_exactly_board_fill_intake` is how that test reports its full list once a second violator turns up. The `ExemptRootType` exemption at `tests/.../OrchestrationCliCommandViewArchitectureTests.cs:9` is unchanged.
        - **Fix:** belongs in goal 1deac5c0, not here. Either move `WorkerUsageSampleSource` off the `App.Cli` context-usage types, or move those types to a namespace the Orchestration boundary allows. This candidate needs no change.

        **2. `AcceptanceCohortWorkflowTestsLongCohortRepeatedRuns`: train or timing failure**
        - The message is "Sibling tests did not complete their first pass while the train gate was held", at iteration 1 of 20. That is a merge-train orchestration test, and it doesn't touch task-brief rendering.
        - The candidate changes only three files:
          - `AgentOrchestratorKernel.TaskBriefs.cs`: −64 lines, +1 call
          - the new `RefinedSpecBriefSection.cs`
          - `TaskBriefTests.cs`
        - None of these is on the cohort or train path. The most likely explanation is a timing failure under train load, or fallout from the 1deac5c0 member. To confirm, run that test alone on the candidate before treating it as attributable.

        **Smallest fix for this goal:** none to the code. Re-gate this candidate on its own, without 1deac5c0, or after 1deac5c0 is corrected. A `reopen-regate` adjudication is enough, citing this gate's failure records and the train-membership evidence above.

        **What must stay unchanged:**
        - **Criterion 4:** the `TaskBriefs.cs` diff is exactly the removed inline block plus the single call `RefinedSpecBriefSection.AppendTo(segments, goal, task.RequiredRole, declarationContext);`.
        - **The architecture tests' exemption list:** do not add `WorkerUsageSampleSource` to it to make the gate pass.
        - **The long-cohort test:** do not edit it from this goal.
        """;

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
