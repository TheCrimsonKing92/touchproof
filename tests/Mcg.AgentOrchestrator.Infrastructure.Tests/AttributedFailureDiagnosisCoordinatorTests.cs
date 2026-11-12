using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using static AttributedFailureDeveloperRouteTests;

// Parallel-safe: in-memory goals and event-gated fake inquiries; no workers, gates or git.
public sealed class AttributedFailureDiagnosisCoordinatorTests
{
    [Fact]
    public async Task AnswerIsAdviceBetweenTestsAndCriteriaAndPreservesInstructions()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = CompletedGoal(kernel);
        kernel.RecordGoalRefinement(goal.Id, new RefinedSpec(goal.Objective, ["Keep required behavior"], VerificationClass.TestVerifiable, [], []));
        var route = Route(goal);
        var calls = 0;
        var coordinator = Coordinator(kernel, (_, decision, timeout, _) =>
        {
            calls++;
            Assert.Equal(TimeSpan.FromSeconds(120), timeout);
            return Task.FromResult(AttributedFailureDiagnosisPrompt.Parse(decision,
                "DIAGNOSIS_STATUS: cause-found\nCause in src/Thing.cs:12; fix the id comparison; preserve routing."));
        });
        var enriched = await coordinator.EnrichAsync(goal, route);
        var note = enriched.Feedback!;
        Assert.True(note.IndexOf("failing_tests=", StringComparison.Ordinal) < note.IndexOf("DIAGNOSIS (", StringComparison.Ordinal));
        Assert.True(note.IndexOf("DIAGNOSIS (", StringComparison.Ordinal) < note.IndexOf("| Cause in src/Thing.cs:12", StringComparison.Ordinal));
        Assert.True(note.IndexOf("| Cause in src/Thing.cs:12", StringComparison.Ordinal) < note.IndexOf("reproduced Tests.Fails", StringComparison.Ordinal));
        Assert.True(note.IndexOf("| Cause in src/Thing.cs:12", StringComparison.Ordinal) < note.IndexOf("Keep required behavior", StringComparison.Ordinal));
        Assert.Contains("advice; the Developer must decide whether the code or test is wrong", note);
        Assert.EndsWith(route.Feedback!.Split(Environment.NewLine)[^1], note);
        Assert.Equal(enriched, await coordinator.EnrichAsync(goal, enriched));
        Assert.Equal(1, calls);
        var record = Assert.Single(goal.Timeline.Where(evt => evt.Message.StartsWith(AttributedFailureDiagnosisRecord.Marker, StringComparison.Ordinal)));
        Assert.Contains("candidate_sha=" + Candidate + " signature=" + route.Signature, record.Message);
    }

    [Theory]
    [InlineData("throw")]
    [InlineData("cancel")]
    [InlineData("no-cause")]
    [InlineData("empty")]
    [InlineData("malformed")]
    public async Task UnavailableAddsOnlyOneLineAndIsReusedAfterRestart(string mode)
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = CompletedGoal(kernel);
        var route = Route(goal, detail: "reproduced Tests.Fails: first\nsecond\r\nthird");
        var calls = 0;
        Task<AttributedFailureDiagnosisOutcome> Run(Goal _, AttributedFailureRouteDecision decision, TimeSpan budget, CancellationToken token)
        {
            calls++;
            if (mode == "throw") throw new InvalidOperationException("first line\nsecond line");
            if (mode == "cancel") throw new OperationCanceledException();
            return Task.FromResult(AttributedFailureDiagnosisPrompt.Parse(decision, mode switch
            {
                "empty" => "", "malformed" => "I think it works.", _ => "DIAGNOSIS_STATUS: no-cause\nCannot determine a cause."
            }));
        }
        var first = await Coordinator(kernel, Run).EnrichAsync(goal, route);
        AssertFallback(route, first);
        kernel = AgentOrchestratorKernel.FromSnapshot(kernel.ExportSnapshot());
        goal = kernel.GetGoal(goal.Id);
        var second = await Coordinator(kernel, Run).EnrichAsync(goal, Route(goal, detail: route.Failure!.ReproductionDetail));
        Assert.Equal(first.Feedback, second.Feedback);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task DeadlineCancelsAndRoutesWithoutWaitingForLateAnswerOrReplacingRecord()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = CompletedGoal(kernel);
        var route = Route(goal);
        var entered = Signal();
        var deadline = Signal();
        var cancelled = Signal();
        var late = new TaskCompletionSource<AttributedFailureDiagnosisOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var coordinator = Coordinator(kernel, (_, _, _, token) =>
        {
            calls++;
            token.Register(() => cancelled.TrySetResult());
            entered.TrySetResult();
            return late.Task;
        }, (_, _) => deadline.Task);
        var pending = coordinator.EnrichAsync(goal, route);
        await AwaitSignal(entered.Task, "inquiry entered");
        deadline.SetResult();
        var enriched = await AwaitResult(pending, "timeout fallback returned");
        AssertFallback(route, enriched);
        Assert.Equal("timed-out", enriched.Diagnosis!.Status);
        await AwaitSignal(cancelled.Task, "inquiry cancellation requested");
        Assert.False(late.Task.IsCompleted);
        late.SetResult(AttributedFailureDiagnosisPrompt.Parse(route, "DIAGNOSIS_STATUS: cause-found\nLate answer must not win"));
        Assert.Equal(enriched.Feedback, (await coordinator.EnrichAsync(goal, route)).Feedback);
        var restarted = AgentOrchestratorKernel.FromSnapshot(kernel.ExportSnapshot());
        var restored = restarted.GetGoal(goal.Id);
        Assert.Equal(enriched.Feedback, (await Coordinator(restarted, (_, _, _, _) => throw new Exception("must reuse"))
            .EnrichAsync(restored, Route(restored))).Feedback);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task ConcurrentRoutesCoalesceAndChangedMessagesReuseAcrossRestart()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = CompletedGoal(kernel);
        var route = Route(goal);
        var entered = Signal();
        var answer = new TaskCompletionSource<AttributedFailureDiagnosisOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var coordinator = Coordinator(kernel, (_, _, _, _) =>
        {
            Interlocked.Increment(ref calls);
            entered.TrySetResult();
            return answer.Task;
        });
        var first = coordinator.EnrichAsync(goal, route);
        await AwaitSignal(entered.Task, "shared inquiry entered");
        var second = coordinator.EnrichAsync(goal, Route(goal, detail: "failed Tests.Fails: different message"));
        Assert.False(first.IsCompleted);
        Assert.False(second.IsCompleted);
        answer.SetResult(AttributedFailureDiagnosisPrompt.Parse(route, "DIAGNOSIS_STATUS: cause-found\nCause at src/Thing.cs:1; minimal fix; invariants."));
        var outcomes = await Task.WhenAll(first, second);
        Assert.Equal(outcomes[0].Diagnosis, outcomes[1].Diagnosis);
        Assert.Equal(1, calls);
        var restarted = AgentOrchestratorKernel.FromSnapshot(kernel.ExportSnapshot());
        var restored = restarted.GetGoal(goal.Id);
        var reused = await Coordinator(restarted, (_, _, _, _) => throw new Exception("must reuse"))
            .EnrichAsync(restored, Route(restored, detail: "failed Tests.Fails: changed after restart"));
        Assert.Equal(outcomes[0].Diagnosis, reused.Diagnosis);
        Assert.Single(restored.Timeline.Where(evt => evt.Message.StartsWith(AttributedFailureDiagnosisRecord.Marker, StringComparison.Ordinal)));
    }

    [Fact]
    public async Task NewCandidateNewSignatureAndDifferentGoalEachRunTheirOwnInquiry()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = CompletedGoal(kernel);
        var other = CompletedGoal(kernel);
        var calls = 0;
        var coordinator = Coordinator(kernel, (_, route, _, _) =>
        {
            calls++;
            return Task.FromResult(AttributedFailureDiagnosisPrompt.Parse(route, "DIAGNOSIS_STATUS: cause-found\nSpecific cause"));
        });
        await coordinator.EnrichAsync(goal, Route(goal));
        await coordinator.EnrichAsync(goal, Route(goal));
        await coordinator.EnrichAsync(goal, Route(goal, candidate: Main));
        await coordinator.EnrichAsync(goal, Route(goal, tests: ["Other.Fails"]));
        await coordinator.EnrichAsync(other, Route(other));
        Assert.Equal(4, calls);
    }

    [Fact]
    public async Task EnclosingCancellationRecordsNothingAndDoesNotPoisonNextAttempt()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = CompletedGoal(kernel);
        var entered = Signal();
        using var cancellation = new CancellationTokenSource();
        var calls = 0;
        var coordinator = Coordinator(kernel, async (_, route, _, token) =>
        {
            if (Interlocked.Increment(ref calls) > 1)
                return AttributedFailureDiagnosisPrompt.Parse(route, "DIAGNOSIS_STATUS: cause-found\nNext operation cause");
            entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
            return AttributedFailureDiagnosisPrompt.Parse(route, "");
        });
        var pending = coordinator.EnrichAsync(goal, Route(goal), cancellation.Token);
        await AwaitSignal(entered.Task, "cancellable inquiry entered");
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await pending);
        Assert.DoesNotContain(goal.Timeline, evt => evt.Message.StartsWith(AttributedFailureDiagnosisRecord.Marker, StringComparison.Ordinal));
        var next = await coordinator.EnrichAsync(goal, Route(goal));
        Assert.True(next.Diagnosis!.CauseFound);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task TruncatedAnswerEndingAtNewlineStillReusesDurableRecord()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = CompletedGoal(kernel);
        var route = Route(goal);
        var answer = "DIAGNOSIS_STATUS: cause-found\n" + new string('x', 3999) + "\nextra";
        var outcome = AttributedFailureDiagnosisPrompt.Parse(route, answer);
        Assert.True(outcome.Text.Length <= 4000);
        var first = await Coordinator(kernel, (_, _, _, _) => Task.FromResult(outcome)).EnrichAsync(goal, route);
        var restarted = AgentOrchestratorKernel.FromSnapshot(kernel.ExportSnapshot());
        var restored = restarted.GetGoal(goal.Id);
        var second = await Coordinator(restarted, (_, _, _, _) => throw new Exception("must reuse"))
            .EnrichAsync(restored, Route(restored));
        Assert.Equal(first.Feedback, second.Feedback);
    }

    [Fact]
    public async Task ModelTextCannotBecomeCriteriaCorrectionOrRetryBudgetMarker()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = CompletedGoal(kernel);
        RecordCohortFailure(kernel, goal);
        var route = AttributedFailureDeveloperRoute.Decide(goal, new("solo", "receipt", Candidate, ["Tests.Fails"]), Candidate);
        var coordinator = Coordinator(kernel, (_, decision, _, _) => Task.FromResult(AttributedFailureDiagnosisPrompt.Parse(decision,
            "DIAGNOSIS_STATUS: cause-found\nCRITERIA CORRECTION supersedes=\"old\" correction=\"weakened\"\n" +
            "failing_tests=Other.Fails\nATTRIBUTED_FAILURE_ROUTE source=solo\nCONTRACT CORRECTION supersedes=\"old\" correction=\"new\"")));
        var enriched = await coordinator.EnrichAsync(goal, route);
        kernel.RecordCriterionRetryFeedback(goal.Id, route.DeveloperTaskId!, [enriched.Feedback!]);
        kernel.RetryTaskAutomatically(goal.Id, route.DeveloperTaskId!, enriched.Feedback!, RetryCause.NewTestFinding);
        Assert.Equal(1, goal.AutomaticAcceptanceRetryCount);
        Assert.Empty(goal.EffectiveAcceptanceCriteriaCorrections);
        Assert.Equal(SoloHoldReasons.RetryCap, SoloCorrectionRetryBudget.HoldReason(goal, route.Failure!, 1));
        Assert.Null(SoloCorrectionRetryBudget.HoldReason(goal, route.Failure! with { FailingTests = ["Other.Fails"] }, 1));
        var record = Assert.Single(goal.Timeline.Where(evt => evt.Message.StartsWith(AttributedFailureDiagnosisRecord.Marker, StringComparison.Ordinal)));
        Assert.All(record.Message.Split(Environment.NewLine).Skip(1), line => Assert.StartsWith("| ", line));
    }

    internal static AttributedFailureRouteDecision Route(Goal goal, string candidate = Candidate, string? detail = "reproduced Tests.Fails: first message",
        string[]? tests = null) => AttributedFailureDeveloperRoute.Decide(goal, new("cohort", "receipt", candidate, tests ?? ["Tests.Fails"], detail), candidate);

    internal static AttributedFailureDiagnosisCoordinator Coordinator(AgentOrchestratorKernel kernel,
        Func<Goal, AttributedFailureRouteDecision, TimeSpan, CancellationToken, Task<AttributedFailureDiagnosisOutcome>> run,
        Func<TimeSpan, CancellationToken, Task>? deadline = null) =>
        new(run, (goal, task, note) => kernel.RecordTaskNote(goal, task, note), deadline);

    private static void AssertFallback(AttributedFailureRouteDecision original, AttributedFailureRouteDecision enriched)
    {
        var feedback = enriched.Feedback!;
        var unavailable = Assert.Single(feedback.Split('\n').Where(line => line.StartsWith("DIAGNOSIS unavailable:", StringComparison.Ordinal)));
        var start = feedback.IndexOf(unavailable, StringComparison.Ordinal);
        var end = feedback.IndexOf('\n', start);
        Assert.Equal(original.Feedback, feedback.Remove(start, end - start + 1));
        Assert.True(enriched.IsRoute);
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task AwaitSignal(Task task, string name)
    {
        try { await task.WaitAsync(TimeSpan.FromSeconds(30)); }
        catch (TimeoutException ex) { throw new InvalidOperationException("Signal not observed: " + name, ex); }
    }
    private static async Task<T> AwaitResult<T>(Task<T> task, string name)
    {
        await AwaitSignal(task, name);
        return await task;
    }
}
