using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each test owns its kernel and any journal lives in a unique directory.
public sealed class AcceptanceFailedRecoveryDispositionTests
{
    private const string Check = "InfrastructureTests";
    private const string Branch = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Main = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string Attempt = "0123456789abcdef0123456789abcdef";
    private static readonly DateTimeOffset FailureTime = DateTimeOffset.UnixEpoch;

    [Fact]
    public void Completed_verified_acceptance_failed_goal_never_projects_accept()
    {
        var (_, goal) = FailedGoal();
        var task = Assert.Single(goal.Tasks);
        Assert.Equal(WorkTaskStatus.Completed, task.Status);
        Assert.True(task.LastVerification?.Succeeded);
        var disposition = new GoalOperatorDispositionSurface().Evaluate(goal, 0, verificationSatisfied: true);

        Assert.NotEqual(OperatorDispositionState.Accept, disposition.State);
        Assert.Contains("acceptance-gate-failed", disposition.Blockers);
    }

    [Fact]
    public void Environmental_failure_offers_confirmed_acceptance_retry()
    {
        var (_, goal) = FailedGoal(environmental: true, secondTaskStatus: WorkTaskStatus.Cancelled);

        var disposition = Project(goal);

        Assert.Equal(OperatorDispositionState.Recover, disposition.State);
        Assert.Equal(
            $"acceptance-retry {goal.Id.Value[..8]} \"environmental apparatus failure in {Check} at branch {Branch[..12]} main {Main[..12]}\" --confirm-acceptance-retry",
            disposition.NextSafeCommand);
        Assert.Contains("environmental apparatus", disposition.Reason);
        Assert.Contains(Check, disposition.Reason);
    }

    [Fact]
    public void Owner_question_hold_offers_answer_instead_of_acceptance_retry()
    {
        const string requestId = "11111111111111111111111111111111";
        const string question = "Which repair should the conductor use?";
        var hold = new GoalHoldSnapshot("author-hold", "author-owner-question",
            $"author-owner-question item=HumanInput:{requestId} reason=model-ask-owner question={question} recommendation=Repair the tool",
            FailureTime);
        var (_, goal) = FailedGoal(environmental: true, hold: hold);

        var disposition = Project(goal);

        Assert.Equal(OperatorDispositionState.Blocked, disposition.State);
        Assert.Equal($"answer {requestId} --text-file <path>", disposition.NextSafeCommand);
        Assert.Contains(question, disposition.Reason);
        Assert.Contains(Check, disposition.Reason);
        Assert.Contains("acceptance-gate-failed", disposition.Blockers);
        Assert.Contains(hold.State, disposition.Blockers);
        Assert.Contains(disposition.Evidence, pointer => pointer.Kind == "acceptance-attempt" && pointer.Path == Attempt);
        Assert.Contains(disposition.Evidence, pointer => pointer.Kind == "goal-hold" && pointer.Path == hold.Identity);
    }

    [Theory]
    [InlineData(false, OperatorDispositionState.Blocked, "Restore the contracted tool; the conductor retries automatically.")]
    [InlineData(true, OperatorDispositionState.Wait, "")]
    public void Environment_hold_preserves_gate_failure_and_live_gate_precedence(
        bool gateInFlight, OperatorDispositionState expectedState, string expectedAction)
    {
        var hold = CreateEnvironmentHold();
        var (_, goal) = FailedGoal(environmental: true, hold: hold);

        var disposition = new GoalOperatorDispositionSurface().Evaluate(
            goal, 0, verificationSatisfied: true, acceptanceGateInFlight: gateInFlight);

        Assert.Equal(expectedState, disposition.State);
        Assert.Equal(expectedAction, disposition.NextSafeCommand);
        Assert.Contains(Check, disposition.Reason);
        Assert.Contains("acceptance-gate-failed", disposition.Blockers);
        Assert.Contains(EnvironmentHold.State, disposition.Blockers);
        Assert.Contains(disposition.Evidence, pointer => pointer.Kind == "acceptance-attempt" && pointer.Path == Attempt);
        Assert.Contains(disposition.Evidence, pointer => pointer.Kind == "goal-hold" && pointer.Path == hold.Identity
            && pointer.Detail == hold.Blocker);
    }

    [Fact]
    public void Candidate_code_failure_offers_attempt_triage_without_acceptance_retry()
    {
        var (_, goal) = FailedGoal();

        var disposition = Project(goal);

        Assert.Equal(OperatorDispositionState.Blocked, disposition.State);
        Assert.Equal($"gate-stats failure --attempt {Attempt}", disposition.NextSafeCommand);
        Assert.Contains("candidate-code failure", disposition.Reason);
        Assert.Contains($"branch head: {Branch}", disposition.Reason);
        Assert.Contains($"main head: {Main}", disposition.Reason);
        Assert.Contains($"adjudicate --goal {goal.Id.Value[..8]} <task#> reopen-regate|route|close", disposition.Reason);
        Assert.Contains($"recover {goal.Id.Value[..8]} <note>", disposition.Reason);
        Assert.Contains(disposition.Evidence, pointer => pointer.Kind == "acceptance-failure" && pointer.Path == goal.Id.Value);
        Assert.Contains(disposition.Evidence, pointer => pointer.Kind == "acceptance-attempt" && pointer.Path == Attempt);
        Assert.Contains(disposition.Evidence, pointer => pointer.Kind == "solo-failure-receipt" && pointer.Path == "receipt.json"
            && pointer.Detail.Contains("sha256=receipt-digest", StringComparison.Ordinal));
    }

    [Fact]
    public void Exhausted_retry_cap_offers_triage_without_acceptance_retry()
    {
        var (_, goal) = FailedGoal(environmental: true, regates: Goal.OperatorAcceptanceRegateCap);

        var disposition = Project(goal);

        Assert.Equal($"gate-stats failure --attempt {Attempt}", disposition.NextSafeCommand);
        Assert.Contains("exhausted retry cap", disposition.Reason);
        Assert.Contains("acceptance-retry-cap-exhausted", disposition.Blockers);
    }

    [Fact]
    public void Missing_receipt_offers_failure_listing_without_acceptance_retry()
    {
        var (_, goal) = FailedGoal(missingReceipt: true, regates: Goal.OperatorAcceptanceRegateCap);

        var disposition = Project(goal);

        Assert.Equal("gate-stats failures", disposition.NextSafeCommand);
        Assert.Contains("missing receipt", disposition.Reason);
        Assert.Contains("exhausted retry cap", disposition.Reason);
        Assert.Empty(disposition.Evidence);
    }

    [Theory]
    [InlineData(WorkTaskStatus.Pending, true)]
    [InlineData(WorkTaskStatus.Completed, false)]
    public void Unmet_task_prerequisites_block_environmental_retry(WorkTaskStatus status, bool verified)
    {
        var (_, goal) = FailedGoal(environmental: true, attemptId: null, taskStatus: status, verified: verified);

        var disposition = new GoalOperatorDispositionSurface().Evaluate(goal, 0, verificationSatisfied: true);

        Assert.Equal("gate-stats failures", disposition.NextSafeCommand);
        Assert.Contains("unmet task prerequisites", disposition.Reason);
        Assert.Contains($"task 1 ({status}", disposition.Reason);
        Assert.Contains("acceptance-task-prerequisites-unmet", disposition.Blockers);
    }

    [Fact]
    public void Live_replacement_gate_reports_gate_in_flight_without_command()
    {
        var (_, goal) = FailedGoal(environmental: true);
        var root = Directory.CreateTempSubdirectory("failed-acceptance-live-").FullName;
        try
        {
            Assert.False(NextActionsTextView.HasLiveReplacementGate(root, goal));
            GoalOperationJournal.Begin(root, goal, "acceptance");
            GoalOperationJournal.Completed(root, goal, "status");

            var live = NextActionsTextView.HasLiveReplacementGate(root, goal);
            Assert.True(live);
            var disposition = new GoalOperatorDispositionSurface().Evaluate(
                goal, 0, verificationSatisfied: true, acceptanceGateInFlight: live);

            Assert.Equal(OperatorDispositionState.Wait, disposition.State);
            Assert.Empty(disposition.NextSafeCommand);
            Assert.Contains("acceptance-gate-failed", disposition.Blockers);
            Assert.Contains("acceptance-gate-in-flight", disposition.Blockers);
            Assert.Contains("replacement acceptance gate in flight", disposition.Reason);
            Assert.Contains(disposition.Evidence, pointer => pointer.Kind == "acceptance-attempt" && pointer.Path == Attempt);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Journal_gate_begin_closed_by_candidate_outcome_is_not_live()
    {
        var (_, goal) = FailedGoal();
        var root = Directory.CreateTempSubdirectory("failed-acceptance-journal-").FullName;
        try
        {
            GoalOperationJournal.Begin(root, goal, "conductor:acceptance");
            Assert.True(NextActionsTextView.HasLiveReplacementGate(root, goal));
            GoalOperationJournal.AcceptanceFailed(root, goal, "conductor:acceptance", Branch, Main,
                attemptStartedAt: FailureTime.AddMinutes(1));

            Assert.False(NextActionsTextView.HasLiveReplacementGate(root, goal));

            GoalOperationJournal.Begin(root, goal, "conductor:acceptance");
            Assert.True(NextActionsTextView.HasLiveReplacementGate(root, goal));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Monitor_fallback_names_failed_gate_and_recovery_command(bool hasEnvironmentHold)
    {
        var (kernel, goal) = FailedGoal(environmental: hasEnvironmentHold,
            hold: hasEnvironmentHold ? CreateEnvironmentHold() : null);
        var expectedAction = hasEnvironmentHold
            ? "Restore the contracted tool; the conductor retries automatically."
            : $"gate-stats failure --attempt {Attempt}";

        var item = Assert.Single(kernel.BuildNextActions(goal.Id).Items);

        Assert.Equal(NextActionKind.MonitorGoal, item.Kind);
        Assert.DoesNotContain("No immediate action is required", item.Message);
        Assert.Contains("Acceptance gate failed", item.Message);
        Assert.Contains(Check, item.Message);
        Assert.Contains(expectedAction, item.Message);
        Assert.Equal(expectedAction, Project(goal).NextSafeCommand);
        Assert.DoesNotContain("acceptance-retry", item.Message);
    }

    [Theory]
    [InlineData("author-owner-question", "author-owner-question item=Clarification:11111111111111111111111111111111 reason=model-ask-owner question=Which repair should the conductor use? recommendation=Repair the tool")]
    [InlineData("steward-owner-question", "steward-owner-question case=A trigger=A:task:dispatch:1 question=Which repair should the conductor use? evidence=[]")]
    public void Monitor_fallback_points_to_owner_question_instead_of_acceptance_retry(string state, string blocker)
    {
        var hold = new GoalHoldSnapshot("owner-hold", state, blocker, FailureTime);
        var (kernel, goal) = FailedGoal(environmental: true, hold: hold);

        var item = Assert.Single(kernel.BuildNextActions(goal.Id).Items);

        Assert.Equal(NextActionKind.MonitorGoal, item.Kind);
        Assert.Contains("Acceptance gate failed", item.Message);
        Assert.Contains($"attention show {goal.Id.Value[..8]}.", item.Message);
        Assert.DoesNotContain("acceptance-retry", item.Message);
        Assert.DoesNotContain("gate-stats", item.Message);
    }

    private static GoalHoldSnapshot CreateEnvironmentHold()
    {
        var hold = new EnvironmentHold("project", "crosswalk", "crosswalk --version", "required tool",
            null, "tool missing", "tool-fingerprint", "missing");
        return new GoalHoldSnapshot(hold.Identity, EnvironmentHold.State, hold.Format(), FailureTime);
    }

    private static GoalOperatorDisposition Project(Goal goal) =>
        new GoalOperatorDispositionSurface().Evaluate(goal, 0,
            verificationSatisfied: goal.Tasks.All(task => task.LastVerification?.Succeeded == true));

    private static (AgentOrchestratorKernel Kernel, Goal Goal) FailedGoal(
        bool environmental = false,
        bool missingReceipt = false,
        int regates = 0,
        string? attemptId = Attempt,
        WorkTaskStatus taskStatus = WorkTaskStatus.Completed,
        bool verified = true,
        WorkTaskStatus? secondTaskStatus = null,
        GoalHoldSnapshot? hold = null)
    {
        var kernel = new AgentOrchestratorKernel();
        var tasks = new List<TaskSpec> { new(TaskId.New(), "Implement change", AgentRole.Developer) };
        if (secondTaskStatus is not null)
        {
            tasks.Add(new TaskSpec(TaskId.New(), "Additional work", AgentRole.Tester));
        }

        var goal = kernel.CreateGoal("Project failed acceptance", tasks);
        var snapshot = kernel.ExportGoalSnapshot(goal.Id);
        var verification = new TaskVerificationSnapshot("focused check", ".", 0, "passed", "", FailureTime);
        var attribution = environmental
            ? new AcceptanceCheckAttribution(Check, AcceptanceFailureOrigin.Unattributed, "apparatus fault", AcceptanceFailureCause.EnvironmentalApparatus)
            : new AcceptanceCheckAttribution(Check, AcceptanceFailureOrigin.Introduced, "candidate regression");
        var soloReceipt = attemptId is null ? null : new SoloAcceptanceFailureReceipt(
            1, goal.Id.Value, attemptId, Branch, Branch, Main, Main, "results", new("receipt.json", "receipt-digest"));
        kernel.ReplaceGoalWithSnapshot(snapshot with
        {
            Status = GoalStatus.AcceptanceFailed,
            CurrentHold = hold,
            Tasks = snapshot.Tasks.Select((task, index) => index == 0
                ? task with { Status = taskStatus, LastVerification = verified ? verification : null }
                : task with { Status = secondTaskStatus!.Value }).ToArray(),
            LatestAcceptanceFailure = missingReceipt ? null : new AcceptanceFailureSnapshot(
                FailureTime, [Check], Branch, Main, [attribution], AttemptId: attemptId, SoloFailureReceipt: soloReceipt),
            OperatorAcceptanceRegateCount = regates
        });
        return (kernel, kernel.GetGoal(goal.Id));
    }
}
