using Mcg.AgentOrchestrator.App.Application;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;
using static ConductorDriverTests;

// Parallel-safe: in-memory kernels, fake starts, and privately owned artifact directories.
public sealed class SandboxPreparationFailureReportingTests
{
    private static string Reason(bool timedOut = true) => WorkerSandboxLabelFailureReason.Build(
        "worktree", null, IntegrityLabelCommandResult.Completed(!timedOut, timedOut ? null : 1,
            true, new(false), new(false)));

    [Theory]
    [InlineData("\r\n", true)]
    [InlineData("\n", false)]
    public void Read_HostDiagnostic_PreservesFirstLineAndNativeTimeout(string newline, bool timedOut)
    {
        var reason = Reason(timedOut);
        var verification = Verification(reason, newline);
        var failure = Assert.IsType<SandboxPreparationFailure>(SandboxPreparationFailure.Read(verification));
        Assert.Equal(reason, failure.FirstLine);
        Assert.Equal(timedOut, failure.NativeCommandTimedOut);
    }

    [Fact]
    public void Read_TruncatedNativeOutputTail_StillReadsStructuredTimeout()
    {
        const string reason = "Failed to apply inheritable Low integrity label to 'worktree'. " +
            "Native label command: {\"completed\":false,\"exitCode\":null,\"timedOut\":true,\"stdoutTail\":\"truncated";
        Assert.True(SandboxPreparationFailure.Read(Verification(reason))!.NativeCommandTimedOut);
        var spoof = "Failed to apply inheritable Low integrity label to 'worktree'. " +
            "Native label command: {\"stdoutTail\":\"timedOut:true\",\"timedOut\":false}";
        Assert.False(SandboxPreparationFailure.Read(Verification(spoof))!.NativeCommandTimedOut);
    }

    [Fact]
    public void ApplyAdmission_SecondPreparationFailure_HoldIncludesDiagnostic()
    {
        var (kernel, goal) = SimpleGoal();
        var task = goal.Tasks.Single();
        DispatchTask(kernel, goal, task);
        task.RecordVerification(Verification(Reason()));
        var at = task.LastDispatch!.DispatchedAt;
        var receipt = new RetryAdmissionReceipt("prep-held", RetryCause.UnchangedContextRepeat,
            new(1, "same-context"), RetryAdmissionDecision.Prevented, RetryAdmissionRoute.EnvironmentalHold,
            PaidRouteClassification.Paid, at, at, PriorAttemptAt: at.AddMinutes(-1));

        kernel.ApplyPreparedRetryAdmission(goal.Id, task.Id, new(RetryAdmissionDecision.Prevented, receipt));

        var question = Assert.Single(kernel.GetPendingHumanInput(goal.Id));
        Assert.Equal(HumanWaitKind.RecoveryChoice, question.Kind);
        Assert.Contains(Reason(), question.Question);
    }

    [Fact]
    public void Advance_PreparationFailure_RecordsNoteAndEscalationWithoutChangingRetry()
    {
        var (kernel, goal) = SimpleGoal();
        var task = goal.Tasks.Single();
        DispatchTask(kernel, goal, task);
        var reason = Reason();
        kernel.RecordDispatchExecutionResult(goal.Id, task.Id, Verification(reason) with
        {
            CompletedAt = task.LastDispatch!.DispatchedAt.AddSeconds(1),
            DispatchStartedAt = task.LastDispatch.DispatchedAt
        });
        var notes = new List<string>();
        string? escalation = null;
        string? retry = null;
        var driver = MakeDriver(getFacts: _ => GoalLifecycleFacts.None,
            recordTaskNote: (_, _, text) => notes.Add(text),
            retryTask: (gid, tid, text) => { retry = text; return kernel.RetryTask(gid, tid, text); },
            dispatchAndStart: current => ConductorDriver.ClassifySubscriptionStartForConductor(
                PreparedWithoutStart(current, task)),
            writeEscalation: (_, _, text) => escalation = text);

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.Contains(notes, note => note.Contains(reason, StringComparison.Ordinal));
        Assert.NotNull(retry);
        Assert.DoesNotContain(reason, retry);
        Assert.Contains(reason, escalation);
        Assert.IsType<ConductorAdvanceOutcome.Escalated>(result.Outcome);
    }

    [Fact]
    public void Advance_PreparationHostAlive_ReportsPreparationAndPreviousError()
    {
        var (kernel, goal) = SimpleGoal();
        var task = goal.Tasks.Single();
        task.RecordVerification(Verification(Reason()));
        DispatchTask(kernel, goal, task);
        var root = ConductorDriverTests.CreateTempDirectory();
        try
        {
            var stem = Path.Combine(root, "dispatch");
            var process = new TaskProcessRecord(42, "host", root, stem + ".out.log",
                stem + ".err.log", stem + ".exit.txt", task.LastDispatch!.DispatchedAt.AddSeconds(1), null, null);
            task.RecordProcess(process);
            DispatchProcessHost.WritePrepRecord(stem + ".prep.json", new("sandbox-prep", goal.Id.Value,
                task.Id.Value, root, stem + ".prep.heartbeat.json", stem + ".prep.exit.txt", process.StartedAt,
                WorkerSandboxProvider.Codex, true, null));
            var driver = MakeDriver(getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true));

            var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

            var held = Assert.IsType<ConductorAdvanceOutcome.Held>(result.Outcome);
            Assert.Contains("preparation in progress", held.Reason);
            Assert.Contains(Reason(), held.Reason);
            Assert.DoesNotContain("Worker process running", held.Reason);
            File.WriteAllText(stem + ".prep.exit.txt", "1");
            File.WriteAllText(process.StandardErrorPath, Verification(Reason()).StandardError);
            Assert.Contains(Reason(), SandboxPreparationStartStatus.RunningReason(goal));
            File.WriteAllText(stem + ".prep.exit.txt", "0");
            Assert.Null(SandboxPreparationStartStatus.RunningReason(goal));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static TaskVerificationRecord Verification(string reason, string newline = "\n") =>
        new("host", "worktree", 1, "", "[dispatch-host] worker launch/run failed: " +
            "System.InvalidOperationException: " + reason + newline + "   at Host.Run()",
            DateTimeOffset.Parse("2026-11-12T00:00:00Z"));

    private static SubscriptionStartResult PreparedWithoutStart(Goal goal, TaskSpec task) => new(
        [new(task, "prompt.md")],
        new(new ProcessBatchPlan(goal.Id, goal.Objective, goal.Status, ProcessBatchActionKind.StartDispatches,
            0, 1, []), []), new([], []), []);
}
