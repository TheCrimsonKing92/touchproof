using Mcg.AgentOrchestrator.Core;

// Parallel-safe: all goal, dispatch and clock state is in memory; no process is launched.
public sealed class AnsweredHumanInputRetryContextTests
{
    [Xunit.Fact]
    public void AuthoritativeAnswer_AfterWorkerQuestion_ChangesFingerprintAndRestores()
    {
        var context = CreateContext();
        var before = Fingerprint(context);
        Assert.Equal(context.FirstFingerprint, before);

        context.Kernel.SubmitHumanInput(context.Request.Id, "Use the existing contract.");

        var answered = Fingerprint(context);
        Assert.NotEqual(before, answered);
        var restored = AgentOrchestratorKernel.FromSnapshot(context.Kernel.ExportSnapshot(), context.Clock);
        Assert.Equal(answered, RetryContextFingerprintFactory.Build(
            restored.GetGoal(context.Goal.Id), restored.GetTask(context.Goal.Id, context.Task.Id),
            "fixture", "model", PaidRouteClassification.Paid, null, null, null,
            restored.HumanInputRequests));

        context.Kernel.SupersedeHumanInput(context.Goal.Id, context.Request.Id,
            "Use the corrected contract.", HumanInputAnswerOrigin.Operator);
        Assert.NotEqual(answered, Fingerprint(context));
    }

    [Xunit.Fact]
    public void EntireHistory_ReorderedRequests_PreservesEachRequestIdAndAnswer()
    {
        var context = CreateContext();
        var first = Request(context);
        var second = Request(context);
        first.Complete("Same ruling\u001fwith separators.", context.Clock.UtcNow);
        second.Complete("Same ruling\u001fwith separators.", context.Clock.UtcNow,
            origin: HumanInputAnswerOrigin.Worker, isAutoDefaulted: true);

        var both = Fingerprint(context, [first, second]);
        Assert.Equal(both, Fingerprint(context, [second, first]));
        Assert.NotEqual(Fingerprint(context, [first]), Fingerprint(context, [second]));
        Assert.NotEqual(both, Fingerprint(context, [first]));
        Assert.NotEqual(both, Fingerprint(context, [second]));
        second.Supersede("Revised ruling.", context.Clock.UtcNow, HumanInputAnswerOrigin.Operator);
        Assert.NotEqual(both, Fingerprint(context, [first, second]));
    }

    [Xunit.Theory]
    [Xunit.InlineData("pending")]
    [Xunit.InlineData("dismissed")]
    [Xunit.InlineData("parked")]
    [Xunit.InlineData("superseded-request")]
    [Xunit.InlineData("other-task")]
    [Xunit.InlineData("other-goal")]
    [Xunit.InlineData("goal-level")]
    [Xunit.InlineData("retracted-answer")]
    [Xunit.InlineData("blank-answer")]
    public void IneligibleRequest_WithoutAuthoritativeTaskAnswer_LeavesContextUnchanged(string kind)
    {
        var context = CreateContext();
        var request = new HumanInputRequest(HumanInputRequestId.New(),
            kind == "other-goal" ? GoalId.New() : context.Goal.Id,
            kind == "goal-level" ? null : kind == "other-task" ? TaskId.New() : context.Task.Id,
            "Which contract?", context.Clock.UtcNow,
            supersededByRequestId: kind == "superseded-request" ? HumanInputRequestId.New() : null);
        if (kind == "dismissed")
            request.Dismiss(context.Clock.UtcNow);
        else if (kind != "pending")
            request.Complete("Use the existing contract.", context.Clock.UtcNow);

        var snapshot = request.ToSnapshot();
        if (kind == "parked")
        {
            // Use the same synthetic completion marker as the goal lifecycle.
            context.Kernel.ParkGoal(context.Goal.Id, "Await owner attention.");
            request = context.Request;
            Assert.True(request.IsSyntheticParkedHumanWaitCompletion);
        }
        else if (kind is "retracted-answer" or "blank-answer")
        {
            request = HumanInputRequest.FromSnapshot(snapshot with
            {
                AnswerHistory = snapshot.AnswerHistory!.Select(answer => kind == "blank-answer"
                    ? answer with { Text = " " }
                    : answer with { SupersededByAnswerId = "replacement" }).ToList()
            });
        }

        Assert.Equal(Fingerprint(context, []), Fingerprint(context, [request]));
    }

    [Xunit.Fact]
    public void IdenticalContext_WithoutAnswer_IsStillRefused()
    {
        var context = CreateContext();
        var fingerprint = Fingerprint(context);
        Assert.Equal(context.FirstFingerprint, fingerprint);

        var result = NextAdmission(context, fingerprint);

        Assert.Equal(RetryAdmissionDecision.Prevented, result.Decision);
        Assert.Equal(RetryCause.UnchangedContextRepeat, result.Receipt.Cause);
    }

    [Xunit.Fact]
    public void AnsweredContext_AfterAnotherFailure_RefusesAnUnchangedRepeat()
    {
        var context = CreateContext();
        context.Kernel.SubmitHumanInput(context.Request.Id, "Use the existing contract.");
        var answered = Fingerprint(context);
        Assert.NotEqual(context.FirstFingerprint, answered);
        var admitted = NextAdmission(context, answered);
        Assert.Equal(RetryAdmissionDecision.Allowed, admitted.Decision);
        context.Task.RecordRetryAdmission(admitted.Receipt);
        RecordFailure(context);
        context.Task.RecordRetry(context.Clock.UtcNow, RetryCause.ContractClarification);
        Assert.Equal(answered, Fingerprint(context));

        var repeated = NextAdmission(context, Fingerprint(context));

        Assert.Equal(RetryAdmissionDecision.Prevented, repeated.Decision);
        Assert.Equal(RetryCause.UnchangedContextRepeat, repeated.Receipt.Cause);
    }

    [Xunit.Fact]
    public void NextAttemptBrief_AfterAnswer_ContainsResolvedAnswerText()
    {
        var context = CreateContext();
        const string answer = "Use the existing retry context contract.";
        context.Kernel.SubmitHumanInput(context.Request.Id, answer);
        Assert.Equal(WorkTaskStatus.Assigned, context.Task.Status);

        var prompt = context.Kernel.BuildTaskBriefSource(context.Goal.Id, context.Task.Id)
            .ProjectLegacyMarkedTextV1(false).Content;

        Assert.Contains("## Resolved Human Input", prompt, StringComparison.Ordinal);
        Assert.Contains($"{context.Request.Id}: {context.Request.Question} → {answer}",
            prompt, StringComparison.Ordinal);
    }

    private static Context CreateContext()
    {
        var clock = new FakeClock();
        var kernel = new AgentOrchestratorKernel(clock);
        var task = new TaskSpec(TaskId.New(), "Implement clarified contract", AgentRole.Developer);
        var goal = kernel.CreateGoal("Resume after a worker clarification", [task]);
        kernel.ActivateGoal(goal.Id, DefaultAgents());
        var fingerprint = RetryContextFingerprintFactory.Build(goal, task,
            "fixture", "model", PaidRouteClassification.Paid, null, null, null, kernel.HumanInputRequests);
        kernel.RecordTaskDispatch(goal.Id, task.Id,
            new TaskDispatchRecord("fixture", "unused", "in-memory", clock.UtcNow));
        Assert.Equal(RetryAdmissionDecision.Allowed, kernel.RecordPreparedRetryAdmission(
            goal.Id, task.Id, fingerprint, PaidRouteClassification.Paid, clock.UtcNow).Decision);
        kernel.RecordTaskProcessStarted(goal.Id, task.Id,
            new TaskProcessRecord(4101, "unused", "in-memory", "out", "err", "exit",
                clock.UtcNow, clock.UtcNow.AddSeconds(1), 1));
        var request = kernel.RequestHumanInput(goal.Id, task.Id, "Which contract?");
        var context = new Context(kernel, goal, task, request, clock, fingerprint);
        RecordFailure(context);
        // Isolate answer context: a domain retry marker adds no TaskRetried feedback.
        task.RecordRetry(clock.UtcNow, RetryCause.ContractClarification);
        return context;
    }

    private static void RecordFailure(Context context)
    {
        var startedAt = context.Task.LastDispatch!.DispatchedAt;
        context.Task.MarkRetryAdmissionStarted(startedAt, startedAt);
        context.Clock.Advance();
        context.Kernel.RecordTaskVerification(context.Goal.Id, context.Task.Id,
            new TaskVerificationRecord("worker", "in-memory", 1, "", "Clarification needed.",
                context.Clock.UtcNow, DispatchStartedAt: startedAt));
    }

    private static RetryAdmissionResult NextAdmission(Context context, RetryContextFingerprint fingerprint)
    {
        context.Clock.Advance();
        context.Task.RecordDispatch(new TaskDispatchRecord("fixture", "unused", "in-memory", context.Clock.UtcNow));
        return RetryAdmissionPolicy.Evaluate(context.Task, fingerprint, PaidRouteClassification.Paid,
            RetryCause.ContractClarification, context.Clock.UtcNow, context.Clock.UtcNow);
    }

    private static HumanInputRequest Request(Context context) => new(
        HumanInputRequestId.New(), context.Goal.Id, context.Task.Id, "Which contract?", context.Clock.UtcNow);

    private static RetryContextFingerprint Fingerprint(Context context,
        IEnumerable<HumanInputRequest>? requests = null) =>
        RetryContextFingerprintFactory.Build(context.Goal, context.Task, "fixture", "model",
            PaidRouteClassification.Paid, null, null, null, requests ?? context.Kernel.HumanInputRequests);

    private sealed record Context(AgentOrchestratorKernel Kernel, Goal Goal, TaskSpec Task,
        HumanInputRequest Request, FakeClock Clock, RetryContextFingerprint FirstFingerprint);
}
