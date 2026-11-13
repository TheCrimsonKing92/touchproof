using Mcg.AgentOrchestrator.Core;

// Parallel-safe: each case uses only its own kernel and injected clock, with no I/O.
public sealed class PendingRoutedFindingRetentionTests
{
    private const string Finding = "Repair the routed parser finding from the Tester.";
    private const string EnvironmentMessage = "Sandbox preparation failed before worker launch.";

    [Theory]
    [InlineData(RetryCause.EnvironmentApparatusFailure, false)]
    [InlineData(RetryCause.EnvironmentApparatusFailure, true)]
    [InlineData(RetryCause.ProviderInterruption, false)]
    [InlineData(RetryCause.ProviderInterruption, true)]
    public void Retry_BeforeWorkerStart_PreservesFindingAndResetsTask(
        RetryCause cause, bool admittedDispatch)
    {
        var (kernel, goal, task, clock) = CreateAssignedDeveloper();
        RouteFinding(kernel, goal, task);
        var accepted = task.AcceptedRetryFeedback;
        if (admittedDispatch)
        {
            clock.Advance();
            CompleteFailedDispatch(kernel, goal, task, clock, workerStarted: false);
            Assert.Null(Assert.Single(task.RetryAdmissionHistory).WorkerStartedAt);
            Assert.NotNull(task.LastDispatch);
        }
        else
            Assert.Empty(task.RetryAdmissionHistory);
        var roundKind = task.PendingRetryRoundKind;
        var feedbackRound = task.CriterionRetryFeedbackRoundAt;
        Assert.Equal(RetryCause.NewTestFinding, task.PendingRetryCause);
        clock.Advance();

        kernel.RetryTaskAutomatically(goal.Id, task.Id, EnvironmentMessage, cause);

        Assert.Same(accepted, task.AcceptedRetryFeedback);
        Assert.Equal(Finding, task.AcceptedRetryFeedback!.Message);
        Assert.Equal([Finding], task.CriterionRetryFeedback);
        Assert.Equal(feedbackRound, task.CriterionRetryFeedbackRoundAt);
        Assert.Equal(RetryCause.NewTestFinding, task.PendingRetryCause);
        Assert.Equal(roundKind, task.PendingRetryRoundKind);
        AssertReset(task, clock);
        Assert.Contains(goal.Timeline, evt => evt.TaskId == task.Id &&
            evt.Kind == ProgressKind.TaskRetried && evt.Message == EnvironmentMessage &&
            evt.OccurredAt == clock.UtcNow);
    }

    [Fact]
    public void Retry_SuccessiveApparatusFailures_KeepsOriginalAcceptanceWindow()
    {
        var (kernel, goal, task, clock) = CreateAssignedDeveloper();
        RouteFinding(kernel, goal, task);
        var accepted = task.AcceptedRetryFeedback;
        var acceptedAt = accepted!.AcceptedAt;

        foreach (var cause in new[] { RetryCause.EnvironmentApparatusFailure, RetryCause.ProviderInterruption })
        {
            clock.Advance();
            kernel.RetryTask(goal.Id, task.Id, EnvironmentMessage, cause);

            Assert.Same(accepted, task.AcceptedRetryFeedback);
            Assert.Equal(acceptedAt, task.AcceptedRetryFeedback!.AcceptedAt);
            Assert.Equal([Finding], task.CriterionRetryFeedback);
            Assert.Equal(RetryCause.NewTestFinding, task.PendingRetryCause);
            Assert.Equal(RetryRoundKind.Mechanical, task.PendingRetryRoundKind);
            AssertReset(task, clock);
        }
    }

    [Theory]
    [InlineData(RetryCause.EnvironmentApparatusFailure, false)]
    [InlineData(RetryCause.EnvironmentApparatusFailure, true)]
    [InlineData(RetryCause.ProviderInterruption, false)]
    [InlineData(RetryCause.ProviderInterruption, true)]
    public void Retry_AfterWorkerStart_ReplacesFinding(RetryCause cause, bool sameInstant)
    {
        var (kernel, goal, task, clock) = CreateAssignedDeveloper();
        RouteFinding(kernel, goal, task);
        var acceptedAt = task.AcceptedRetryFeedback!.AcceptedAt;
        if (!sameInstant)
            clock.Advance();
        CompleteFailedDispatch(kernel, goal, task, clock, workerStarted: true);
        var admission = Assert.Single(task.RetryAdmissionHistory);
        Assert.NotNull(admission.WorkerStartedAt);
        Assert.True(admission.LinkedDispatchAt >= acceptedAt);
        Assert.Equal(sameInstant, admission.LinkedDispatchAt == acceptedAt);
        Assert.Equal(RetryCause.NewTestFinding, task.PendingRetryCause);
        clock.Advance();

        kernel.RetryTaskAutomatically(goal.Id, task.Id, EnvironmentMessage, cause);

        Assert.Null(task.AcceptedRetryFeedback);
        Assert.Equal([EnvironmentMessage], task.CriterionRetryFeedback);
        Assert.Equal(cause, task.PendingRetryCause);
        AssertReset(task, clock);
    }

    [Fact]
    public void Retry_StartBeforeAcceptance_PreservesNewFinding()
    {
        var (kernel, goal, task, clock) = CreateAssignedDeveloper();
        CompleteFailedDispatch(kernel, goal, task, clock, workerStarted: true);
        var admission = Assert.Single(task.RetryAdmissionHistory);
        Assert.NotNull(admission.WorkerStartedAt);
        clock.Advance();
        RouteFinding(kernel, goal, task);
        var accepted = task.AcceptedRetryFeedback;
        Assert.True(admission.LinkedDispatchAt < accepted!.AcceptedAt);
        clock.Advance();

        kernel.RetryTask(goal.Id, task.Id, EnvironmentMessage, RetryCause.EnvironmentApparatusFailure);

        Assert.Same(accepted, task.AcceptedRetryFeedback);
        Assert.Equal([Finding], task.CriterionRetryFeedback);
        Assert.Equal(RetryCause.NewTestFinding, task.PendingRetryCause);
    }

    [Fact]
    public void Retry_StartOnAnotherTask_PreservesPendingFinding()
    {
        var (kernel, goal, task, clock) = CreateAssignedDeveloper(includeOtherTask: true);
        RouteFinding(kernel, goal, task);
        var accepted = task.AcceptedRetryFeedback;
        var other = goal.Tasks.Single(candidate => candidate.Id != task.Id);
        clock.Advance();
        CompleteFailedDispatch(kernel, goal, other, clock, workerStarted: true);
        Assert.NotNull(Assert.Single(other.RetryAdmissionHistory).WorkerStartedAt);
        Assert.Empty(task.RetryAdmissionHistory);
        clock.Advance();

        kernel.RetryTask(goal.Id, task.Id, EnvironmentMessage, RetryCause.EnvironmentApparatusFailure);

        Assert.Same(accepted, task.AcceptedRetryFeedback);
        Assert.Equal([Finding], task.CriterionRetryFeedback);
        Assert.Equal(RetryCause.NewTestFinding, task.PendingRetryCause);
    }

    [Fact]
    public void Retry_OtherCause_ReplacesUnactedFinding()
    {
        var (kernel, goal, task, clock) = CreateAssignedDeveloper();
        RouteFinding(kernel, goal, task);
        Assert.Empty(task.RetryAdmissionHistory);
        clock.Advance();
        const string sourceFinding = "Repair the new source finding.";

        kernel.RetryTaskAutomatically(goal.Id, task.Id, sourceFinding, RetryCause.NewSourceFinding);

        Assert.Null(task.AcceptedRetryFeedback);
        Assert.Equal([sourceFinding], task.CriterionRetryFeedback);
        Assert.Equal(RetryCause.NewSourceFinding, task.PendingRetryCause);
    }

    [Fact]
    public void Retry_WithoutAcceptedFeedback_KeepsExistingBehavior()
    {
        var (kernel, goal, task, clock) = CreateAssignedDeveloper();
        Assert.Null(task.AcceptedRetryFeedback);
        Assert.Empty(task.CriterionRetryFeedback);
        clock.Advance();

        kernel.RetryTask(goal.Id, task.Id, EnvironmentMessage, RetryCause.EnvironmentApparatusFailure);

        Assert.Null(task.AcceptedRetryFeedback);
        Assert.Empty(task.CriterionRetryFeedback);
        Assert.Equal(RetryCause.EnvironmentApparatusFailure, task.PendingRetryCause);
        AssertReset(task, clock);
    }

    [Fact]
    public void Brief_AfterApparatusRetry_LeadsWithFindingAndIncludesLaterEvent()
    {
        var (kernel, goal, task, clock) = CreateAssignedDeveloper();
        RouteFinding(kernel, goal, task);
        clock.Advance();
        kernel.RetryTask(goal.Id, task.Id, EnvironmentMessage, RetryCause.EnvironmentApparatusFailure);

        var brief = kernel.BuildTaskBrief(goal.Id, task.Id).Content;

        const string heading = "## Unmet acceptance criteria from the prior attempt - fix these:";
        var start = brief.IndexOf(heading, StringComparison.Ordinal);
        Assert.True(start >= 0);
        var end = brief.IndexOf(Environment.NewLine + Environment.NewLine, start, StringComparison.Ordinal);
        var unmet = brief[start..(end < 0 ? brief.Length : end)];
        Assert.Contains("- " + Finding, unmet, StringComparison.Ordinal);
        Assert.DoesNotContain(EnvironmentMessage, unmet, StringComparison.Ordinal);
        start = brief.IndexOf("<!-- ACCUMULATED_RETRY_FEEDBACK_START -->", StringComparison.Ordinal);
        end = brief.IndexOf("<!-- ACCUMULATED_RETRY_FEEDBACK_END -->", StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        var accumulated = brief[start..end];
        Assert.Contains(Finding, accumulated, StringComparison.Ordinal);
        Assert.Contains("TaskRetried: " + EnvironmentMessage, accumulated, StringComparison.Ordinal);
        Assert.Contains("Most recent retry: Retry 2 of 2;", accumulated, StringComparison.Ordinal);
    }

    private static (AgentOrchestratorKernel Kernel, Goal Goal, TaskSpec Task, FakeClock Clock)
        CreateAssignedDeveloper(bool includeOtherTask = false)
    {
        var clock = new FakeClock();
        var kernel = new AgentOrchestratorKernel(clock);
        var task = new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer);
        TaskSpec[] tasks = includeOtherTask
            ? [task, new TaskSpec(TaskId.New(), "Inspect independent work", AgentRole.Tester)]
            : [task];
        var goal = kernel.CreateGoal("Retain pending routed finding", tasks);
        kernel.ActivateGoal(goal.Id, DefaultAgents());
        return (kernel, goal, task, clock);
    }

    private static void RouteFinding(AgentOrchestratorKernel kernel, Goal goal, TaskSpec task)
    {
        kernel.RetryTaskWithAuthoritativeFeedback(goal.Id, task.Id, Finding, RetryCause.NewTestFinding,
            retryRoundKind: RetryRoundKind.Mechanical);
        Assert.Equal(RetryCause.NewTestFinding, task.PendingRetryCause);
        Assert.Equal(Finding, task.AcceptedRetryFeedback!.Message);
    }

    private static void CompleteFailedDispatch(
        AgentOrchestratorKernel kernel, Goal goal, TaskSpec task, FakeClock clock, bool workerStarted)
    {
        kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("worker", "command", "worktree", clock.UtcNow));
        var admission = kernel.RecordPreparedRetryAdmission(goal.Id, task.Id,
            new RetryContextFingerprint(1, "finding-context"), PaidRouteClassification.NonPaid, clock.UtcNow);
        Assert.True(admission.AllowsProcessStart);
        TaskProcessRecord? process = null;
        if (workerStarted)
        {
            process = new TaskProcessRecord(123, "command", "worktree", "out", "err", "exit", clock.UtcNow, null, null);
            kernel.RecordTaskProcessStarted(goal.Id, task.Id, process);
        }
        clock.Advance();
        var result = new TaskVerificationRecord("command", "worktree", 1, "", "failure", clock.UtcNow);
        if (process is not null)
            kernel.RecordTaskProcessRefreshed(goal.Id, task.Id, process with { CompletedAt = clock.UtcNow, ExitCode = 1 }, result);
        else
            kernel.RecordDispatchExecutionResult(goal.Id, task.Id, result);
        Assert.Equal(WorkTaskStatus.Failed, task.Status);
    }

    private static void AssertReset(TaskSpec task, FakeClock clock)
    {
        Assert.Equal(WorkTaskStatus.Assigned, task.Status);
        Assert.Equal(clock.UtcNow, task.LatestRetryAt);
        Assert.Null(task.LastDispatch);
        Assert.Null(task.LastProcess);
        Assert.Null(task.LastVerification);
        Assert.Null(task.LastExecution);
    }
}
