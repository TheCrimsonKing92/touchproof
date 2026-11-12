using Mcg.AgentOrchestrator.Core;
using Xunit;

// Parallel-safe: pure admission policy with task-local histories; no processes or filesystem.
public sealed class PreparationTimeoutRetryAdmissionTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-11-12T00:00:00Z");
    private static readonly RetryContextFingerprint Fingerprint = new(1, "same-context");

    [Fact]
    public void Evaluate_TimedOutPreparation_AllowsOnceThenHolds()
    {
        var task = new TaskSpec(TaskId.New(), "Prepare", AgentRole.Developer);
        var first = Attempt(task, 0);
        Assert.Equal(RetryAdmissionDecision.Allowed, first.Decision);
        Fail(task, 0, timedOut: true);

        var retry = Attempt(task, 1);
        Assert.Equal(RetryAdmissionDecision.Allowed, retry.Decision);
        Assert.DoesNotContain(task.RetryAdmissionHistory,
            receipt => receipt.Decision == RetryAdmissionDecision.RecoveryEvidence);
        Fail(task, 1, timedOut: true);

        var held = Attempt(task, 2);
        Assert.Equal(RetryAdmissionDecision.Prevented, held.Decision);
        Assert.Equal(RetryCause.UnchangedContextRepeat, held.Receipt.Cause);
        Assert.Equal(RetryAdmissionRoute.EnvironmentalHold, held.Receipt.Route);
    }

    [Fact]
    public void Evaluate_NativeCommandDidNotTimeOut_HoldsImmediately()
    {
        var task = new TaskSpec(TaskId.New(), "Prepare", AgentRole.Developer);
        Attempt(task, 0);
        Fail(task, 0, timedOut: false);
        Assert.Equal(RetryAdmissionDecision.Prevented, Attempt(task, 1).Decision);
    }

    [Fact]
    public void Evaluate_WorkerCompletionThenPreparationFailure_RenewsAllowance()
    {
        var task = new TaskSpec(TaskId.New(), "Prepare", AgentRole.Developer);
        Attempt(task, 0);
        Fail(task, 0, timedOut: true);
        Attempt(task, 1);
        task.RecordVerification(new TaskVerificationRecord("worker", "worktree", 1, "worker ran", "error",
            Start.AddMinutes(1).AddSeconds(2), DispatchStartedAt: Start.AddMinutes(1).AddSeconds(1),
            ChildProcessId: 42));
        // Changed source permits a new worker attempt under the ordinary rules.
        Attempt(task, 2, new(1, "changed-context"));
        Fail(task, 2, timedOut: true);
        Assert.Equal(RetryAdmissionDecision.Allowed, Attempt(task, 3, new(1, "changed-context")).Decision);
    }

    [Fact]
    public void Evaluate_SecondFailureWithChangedFingerprint_StillHolds()
    {
        var task = new TaskSpec(TaskId.New(), "Prepare", AgentRole.Developer);
        Attempt(task, 0);
        Fail(task, 0, timedOut: true);
        Attempt(task, 1, new(1, "retry-message-changed"));
        Fail(task, 1, timedOut: true);
        Assert.Equal(RetryAdmissionDecision.Prevented,
            Attempt(task, 2, new(1, "another-message")).Decision);
    }

    [Fact]
    public void Evaluate_InterveningHostFailure_DoesNotRenewAllowance()
    {
        var task = new TaskSpec(TaskId.New(), "Prepare", AgentRole.Developer);
        Attempt(task, 0);
        Fail(task, 0, timedOut: true);
        Assert.Equal(RetryAdmissionDecision.Allowed, Attempt(task, 1).Decision);
        task.RecordVerification(new TaskVerificationRecord("host", "worktree", 1, "", "host failed",
            Start.AddMinutes(1).AddSeconds(1)));
        Fail(task, 1, timedOut: true);
        Assert.Equal(RetryAdmissionDecision.Prevented, Attempt(task, 2).Decision);
    }

    private static RetryAdmissionResult Attempt(TaskSpec task, int index,
        RetryContextFingerprint? fingerprint = null)
    {
        var at = Start.AddMinutes(index);
        fingerprint ??= Fingerprint;
        task.RecordRetry(at.AddSeconds(-1), RetryCause.EnvironmentApparatusFailure);
        task.ClearLatestVerification();
        task.RecordDispatch(new TaskDispatchRecord("worker", "command", "worktree", at,
            RetryContextFingerprint: fingerprint, PaidRoute: PaidRouteClassification.Paid));
        var result = RetryAdmissionPolicy.Evaluate(task, fingerprint, PaidRouteClassification.Paid,
            RetryCause.EnvironmentApparatusFailure, at, at);
        task.RecordRetryAdmission(result.Receipt);
        // Admission's start stamp tracks the dispatch host; preparation has no worker child.
        if (result.Decision == RetryAdmissionDecision.Allowed)
            task.MarkRetryAdmissionStarted(at, at.AddSeconds(1));
        return result;
    }

    private static void Fail(TaskSpec task, int index, bool timedOut) =>
        task.RecordVerification(new TaskVerificationRecord("worker", "worktree", 1, "",
            "[dispatch-host] worker launch/run failed: System.InvalidOperationException: " +
            "Failed to apply inheritable Low integrity label to 'worktree'. Native label command: " +
            (timedOut ? "{\"completed\":false,\"exitCode\":null,\"timedOut\":true}" :
                "{\"completed\":true,\"exitCode\":1,\"timedOut\":false}"),
            Start.AddMinutes(index).AddSeconds(2),
            DispatchStartedAt: Start.AddMinutes(index).AddSeconds(1)));
}
