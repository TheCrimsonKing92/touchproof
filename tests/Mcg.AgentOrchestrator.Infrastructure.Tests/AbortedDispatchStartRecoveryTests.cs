using System.Text.Json;
using Mcg.AgentOrchestrator.App.Application;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class AbortedDispatchStartRecoveryTests
{
    [Fact]
    public void AdmissionFailureAfterDurableCheckpointDiscardsAndDefersWithoutSameTickRetry()
    {
        var f = new DispatchStartRecoveryTestFixture();
        var predecessor = f.Prepare();
        f.SettleAndAnswer();
        var prepared = f.Prepare(fingerprint: false);
        var marker = f.Task.LatestRetryAt;
        var calls = 0;
        var checkpoints = 0;
        DispatchStartOutcome Start(Goal _, ConductorAutonomyPolicy policy)
        {
            calls++;
            var error = Assert.Throws<AbortedDispatchStartException>(() => new GoalDispatchOperations().StartDispatches(
                f.Kernel, f.Workspace, f.Goal, refreshBeforeStart: false,
                checkpointBeforeWorkerStart: (_, _, _, phase) =>
                {
                    Assert.Equal(DispatchRecordCheckpointPhase.BeforeRetryAdmission, phase);
                    f.Save();
                    var durable = new SqliteOrchestratorStateRepository(f.Workspace.SqliteStatePath)
                        .LoadAsync().GetAwaiter().GetResult().GetTask(f.GoalId, f.TaskId);
                    Assert.Equal(WorkTaskStatus.Running, durable.Status);
                    Assert.Equal(prepared.DispatchedAt, durable.LastDispatch!.DispatchedAt);
                    checkpoints++;
                }));
            Assert.Equal("Prepared subscription dispatch is missing its retry-context fingerprint.", error.InnerException!.Message);
            return DispatchStartOutcome.FromDispatchException(error, "start failed");
        }

        var execution = DispatchStartExecutor.Execute(f.Goal, f.GoalId.Value[..8], ConductorAutonomyPolicy.Permissive,
            GoalLifecycleState.Dispatched, Start, Start,
            _ => throw new InvalidOperationException("No sandbox recovery expected"),
            () => throw new InvalidOperationException("Admission abort must not trigger remediation"), () => f.Goal, _ => { });

        Assert.Equal(DispatchStartOutcomeCategory.Deferred, execution.Outcome.Category);
        Assert.Equal(1, calls);
        Assert.Equal(1, checkpoints);
        Assert.Equal(WorkTaskStatus.Assigned, f.Task.Status);
        Assert.Null(f.Task.LastDispatch);
        Assert.Null(f.Task.LastProcess);
        Assert.Equal(marker, f.Task.LatestRetryAt);
        Assert.Contains(f.Task.DispatchHistory, dispatch => dispatch.DispatchedAt == predecessor.DispatchedAt);
        Assert.True(Assert.Single(f.Kernel.HumanInputRequests).IsCompleted);
        Assert.Contains(f.Goal.Timeline, entry => entry.TaskId == f.TaskId &&
            entry.Kind == ProgressKind.TaskNote && entry.Message == AbortedDispatchStartRecovery.NotePrefix +
            "Prepared subscription dispatch is missing its retry-context fingerprint.");
        Assert.False(AbortedDispatchStartRecovery.IsRepeated(f.Goal, f.TaskId));
        f.Restart();
        Assert.Equal(WorkTaskStatus.Assigned, f.Task.Status);
        Assert.Null(f.Task.LastDispatch);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SecondIdenticalFailureHoldsAcrossRestartAndAppliedRetryClearsIt(bool solo)
    {
        var f = new DispatchStartRecoveryTestFixture(solo);
        if (!solo) f.Kernel.RetryTask(f.GoalId, f.TaskId, "Paid retry", RetryCause.NewSourceFinding);
        Abort(f);
        f.Restart();
        var error = Abort(f);
        Assert.Contains(AbortedDispatchStartRecovery.RepeatedReason, error.Message);
        Assert.Contains("mechanism: retry admission failed twice in a row", error.Message);
        Assert.Contains("owner: operator; next action: read the failure text", error.Message);
        Assert.Equal(WorkTaskStatus.Assigned, f.Task.Status);
        Assert.Null(f.Task.LastDispatch);
        f.Restart();
        var driver = ConductorDriverTests.MakeDriver(getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            dispatchAndStart: _ => throw new InvalidOperationException("Repeated abort must hold before dispatch"));
        driver.BeginTick(f.Kernel, 1);
        var held = Assert.IsType<ConductorAdvanceOutcome.Held>(driver.AdvanceOnce(f.Goal, ConductorAutonomyPolicy.Permissive).Outcome);
        Assert.Contains(AbortedDispatchStartRecovery.RepeatedReason, held.Reason);
        Assert.DoesNotContain("unmapped", held.Reason);

        var store = new CancelDispatchIntentTestStore();
        var payload = new RetryOperatorIntentPayload("Operator classified the failure", null, RetryCause: RetryCause.NewSourceFinding);
        await store.EnqueueAsync(new("retry-abort", "retry-abort", OperatorIntentVerbs.Retry,
            f.GoalId.Value, f.TaskId.Value, JsonSerializer.Serialize(payload, OperatorIntentJson.Options), [],
            "operator", "cli", "local-user", f.Time.UtcNow));
        new OperatorIntentCoordinator(store).ExecutePending(f.Kernel, f.Goal);
        Assert.False(AbortedDispatchStartRecovery.IsRepeated(f.Goal, f.TaskId));
        Assert.DoesNotContain(AbortedDispatchStartRecovery.RepeatedReason, Abort(f).Message);
    }

    [Fact]
    public void DifferentFailureTextBreaksTheStreak()
    {
        var f = new DispatchStartRecoveryTestFixture();
        Abort(f);
        f.Prepare(route: PaidRouteClassification.Unknown);
        var error = Start(f);
        Assert.Contains("missing an explicit paid-route classification", error.InnerException!.Message);
        Assert.False(AbortedDispatchStartRecovery.IsRepeated(f.Goal, f.TaskId));
        Assert.DoesNotContain(AbortedDispatchStartRecovery.RepeatedReason, Abort(f).Message);
    }

    [Theory]
    [InlineData(true, WorkTaskStatus.Completed)]
    [InlineData(false, WorkTaskStatus.Completed)]
    [InlineData(true, WorkTaskStatus.Cancelled)]
    [InlineData(false, WorkTaskStatus.Cancelled)]
    public void TerminalTaskWithRepeatedAbortsDoesNotHoldAfterRestart(bool solo, WorkTaskStatus status)
    {
        var f = new DispatchStartRecoveryTestFixture(solo);
        if (!solo) f.Kernel.RetryTask(f.GoalId, f.TaskId, "Paid retry", RetryCause.NewSourceFinding);
        Abort(f);
        Abort(f);
        Assert.True(AbortedDispatchStartRecovery.IsRepeated(f.Goal, f.TaskId));
        var driver = ConductorDriverTests.MakeDriver(getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            dispatchAndStart: _ => throw new InvalidOperationException("Repeated abort must hold before dispatch"));
        driver.BeginTick(f.Kernel, 1);
        var held = Assert.IsType<ConductorAdvanceOutcome.Held>(driver.AdvanceOnce(f.Goal, ConductorAutonomyPolicy.Permissive).Outcome);
        Assert.Contains(AbortedDispatchStartRecovery.RepeatedReason, held.Reason);

        f.Kernel.ReportTaskProgress(f.GoalId, f.TaskId, status, "Operator resolved the task");
        f.Restart();

        Assert.Equal(status, f.Task.Status);
        Assert.Equal(2, f.Goal.Timeline.Count(entry => entry.TaskId == f.TaskId &&
            entry.Kind == ProgressKind.TaskNote &&
            entry.Message.StartsWith(AbortedDispatchStartRecovery.NotePrefix, StringComparison.Ordinal)));
        Assert.False(AbortedDispatchStartRecovery.IsRepeated(f.Goal, f.TaskId));
        foreach (var (goalStatus, state) in new[]
        {
            (GoalStatus.Active, GoalLifecycleState.AwaitingVerification),
            (GoalStatus.Verifying, GoalLifecycleState.Verifying),
            (GoalStatus.Verified, GoalLifecycleState.Verified)
        })
        {
            var snapshot = f.Kernel.ExportGoalSnapshot(f.GoalId);
            f.Kernel.ReplaceGoalWithSnapshot(snapshot with { Status = goalStatus });
            Assert.Null(NeverStartedDispatchReconciliation.BeforeAdvance(f.Kernel, f.Goal, state,
                ConductorAutonomyPolicy.Permissive,
                _ => throw new InvalidOperationException("A terminal task must not produce a solo start hold")));
        }
    }

    [Theory]
    [InlineData("start")]
    [InlineData("other-abort")]
    [InlineData("route-note")]
    public void InterveningStartOrAbortOrAppliedRouteNoteResetsStreak(string reset)
    {
        var f = new DispatchStartRecoveryTestFixture();
        Abort(f);
        if (reset == "start")
        {
            f.Prepare();
            f.SettleAndAnswer();
        }
        else if (reset == "route-note")
        {
            OperatorRoutingNoteLedger.AcceptNote(f.Kernel, f.Goal, f.Task, "route", "Classified the fault",
                ["evidence"], RetryCause.NewSourceFinding, f.Time.UtcNow);
        }
        else
        {
            var snapshot = f.Kernel.ExportGoalSnapshot(f.GoalId);
            f.Kernel.ReplaceGoalWithSnapshot(snapshot with { Timeline = [.. snapshot.Timeline,
                new(f.GoalId.Value, TaskId.New().Value, ProgressKind.TaskNote,
                    AbortedDispatchStartRecovery.NotePrefix + "other task abort", f.Time.UtcNow)] });
        }
        Assert.DoesNotContain(AbortedDispatchStartRecovery.RepeatedReason, Abort(f).Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UncommittedOrClaimedAttemptCannotBeDiscarded(bool claimed)
    {
        var f = new DispatchStartRecoveryTestFixture();
        var dispatch = f.Prepare();
        if (claimed) f.ClaimCurrent();
        var original = new InvalidOperationException("admission failed");
        var error = Assert.Throws<InvalidOperationException>(() => AbortedDispatchStartRecovery.Admit(
            f.Kernel, f.GoalId, f.TaskId, checkpointCommitted: claimed, () => throw original));
        Assert.Same(original, error);
        Assert.Equal(WorkTaskStatus.Running, f.Task.Status);
        Assert.Equal(dispatch.DispatchedAt, f.Task.LastDispatch!.DispatchedAt);
    }

    private static AbortedDispatchStartException Abort(DispatchStartRecoveryTestFixture f)
    {
        f.Prepare(fingerprint: false, route: PaidRouteClassification.Paid);
        return Start(f);
    }

    private static AbortedDispatchStartException Start(DispatchStartRecoveryTestFixture f) =>
        Assert.Throws<AbortedDispatchStartException>(() => new GoalDispatchOperations().StartDispatches(
            f.Kernel, f.Workspace, f.Goal, refreshBeforeStart: false,
            checkpointBeforeWorkerStart: (_, _, _, _) => f.Save()));
}
