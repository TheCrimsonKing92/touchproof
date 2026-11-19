using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

public sealed class GoalSupervisorUndispatchedAdvisoryTests
{
    private const string UndispatchedReason = "assigned task has no dispatch or execution evidence yet";

    [Fact]
    public void AssignedTasksWithoutDispatchProduceAdvisoryMonitorWithoutGate()
    {
        using var scenario = new Scenario(taskCount: 2);

        var plan = scenario.Supervisor();

        for (var index = 0; index < scenario.Goal.Tasks.Count; index++)
        {
            var task = scenario.Goal.Tasks[index];
            var proposal = Assert.Single(plan.Proposals, candidate => candidate.TaskId == task.Id);
            Assert.Equal(GoalSupervisorProposalKind.Monitor, proposal.Kind);
            Assert.False(proposal.RequiresOperatorGate);
            Assert.False(proposal.CanApply);
            Assert.Equal(UndispatchedReason, proposal.Reason);
            Assert.Equal(index + 1, proposal.TaskNumber);
            Assert.Equal($"subscription-dispatch {index + 1}", proposal.SuggestedCommand);
        }
    }

    [Fact]
    public void ContractCommentNamesEvidenceKindsThatStillGate()
    {
        var source = File.ReadAllText(Path.Combine(VerifiedRepositoryRoot.Find(),
            "src", "Mcg.AgentOrchestrator.App", "Orchestration", "GoalSupervisor.cs"));
        const string commentStart = "// Absence of dispatch evidence alone";
        var start = source.IndexOf(commentStart, StringComparison.Ordinal);
        Assert.True(start >= 0, "The advisory contract comment must be present at the branch.");
        var end = source.IndexOf("if (finding.Status == WorkTaskStatus.Assigned", start, StringComparison.Ordinal);
        Assert.True(end > start, "The contract comment must precede the undispatched-finding branch.");
        var comment = source[start..end];
        foreach (var phrase in new[] { "advisory", "RequiresOperatorGate", "repeated dispatch failure",
                     "subscription-limit review", "retry note", "dirty worktree", "missing verification" })
        {
            Assert.Contains(phrase, comment, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void OperatorInboxHasNoSupervisorBlockerForUndispatchedAssignedTasks()
    {
        using var scenario = new Scenario(taskCount: 2);

        var inbox = scenario.Inbox();

        foreach (var task in scenario.Goal.Tasks)
        {
            Assert.DoesNotContain(inbox.Items, item =>
                item.TaskId == task.Id.Value && item.Kind == OperatorInboxKind.SupervisorProposal &&
                item.Severity == OperatorInboxSeverity.Blocker);
        }
    }

    [Fact]
    public void DrainPlanHasNoOperatorGateForUndispatchedAssignedTasks()
    {
        using var scenario = new Scenario(taskCount: 2);

        var drain = scenario.Drain();

        Assert.DoesNotContain(drain.Items, item => item.Stage == "operator-gate" &&
            (item.Detail == UndispatchedReason || scenario.Goal.Tasks.Select((_, index) =>
                $"subscription-dispatch {index + 1}").Contains(item.SuggestedCommand)));
    }

    [Fact]
    public void FailedTaskRetryNoteStillParksForOperatorWithBlocker()
    {
        using var scenario = new Scenario();
        var task = scenario.Goal.Tasks.Single();
        scenario.Kernel.ReportTaskProgress(scenario.Goal.Id, task.Id, WorkTaskStatus.Failed, "Worker failed.");

        var proposal = Assert.Single(scenario.Supervisor().Proposals, candidate => candidate.TaskId == task.Id);

        Assert.Equal(GoalSupervisorProposalKind.ParkForOperator, proposal.Kind);
        Assert.True(proposal.RequiresOperatorGate);
        Assert.StartsWith("retry ", proposal.SuggestedCommand);
        scenario.AssertInboxBlocker(proposal);
    }

    [Fact]
    public void AssignedTaskWithFailedVerificationKeepsRetryGateAndAdvisory()
    {
        using var scenario = new Scenario();
        var task = scenario.Goal.Tasks.Single();
        scenario.Kernel.RecordTaskVerification(scenario.Goal.Id, task.Id,
            new TaskVerificationRecord("manual verification", scenario.Worktree, 1, "",
                "Focused verification failed.", scenario.Clock.UtcNow));
        Assert.Equal(WorkTaskStatus.Assigned, task.Status);
        Assert.Null(task.LastDispatch);

        var proposals = scenario.Supervisor().Proposals.Where(candidate => candidate.TaskId == task.Id).ToArray();

        Assert.Equal(2, proposals.Length);
        var advisory = Assert.Single(proposals, proposal => proposal.Kind == GoalSupervisorProposalKind.Monitor);
        Assert.False(advisory.RequiresOperatorGate);
        Assert.Equal(UndispatchedReason, advisory.Reason);
        var retry = Assert.Single(proposals, proposal => proposal.Kind == GoalSupervisorProposalKind.ParkForOperator);
        Assert.True(retry.RequiresOperatorGate);
        Assert.StartsWith("retry ", retry.SuggestedCommand);
        scenario.AssertInboxBlocker(retry);
    }

    [Fact]
    public void SubscriptionLimitReviewStillParksForOperatorWithBlocker()
    {
        using var scenario = new Scenario();
        var task = scenario.Goal.Tasks.Single();
        for (var attempt = 0; attempt < 2; attempt++)
        {
            scenario.RecordFailure("ERROR: You've hit your usage limit. Try again in 2 hours.");
            scenario.Kernel.RetryTask(scenario.Goal.Id, task.Id,
                "Retry after the recorded provider window.", RetryCause.ProviderInterruption);
            scenario.Clock.UtcNow = scenario.Clock.UtcNow.AddHours(3);
        }
        Assert.Null(task.LastDispatch);
        Assert.True(ProviderLimitSignals.RequiresSubscriptionLimitReview(task));
        Assert.False(ProviderLimitSignals.IsSubscriptionRetryDeferred(task, DateTimeOffset.UtcNow, out _));

        var proposal = Assert.Single(scenario.Supervisor().Proposals, candidate => candidate.TaskId == task.Id);

        Assert.Equal(GoalSupervisorProposalKind.ParkForOperator, proposal.Kind);
        Assert.True(proposal.RequiresOperatorGate);
        Assert.Contains("--confirm-limit-review", proposal.SuggestedCommand, StringComparison.Ordinal);
        scenario.AssertInboxBlocker(proposal);
    }

    [Fact]
    public void DirtyWorktreeStillParksForOperatorWithBlocker()
    {
        using var scenario = new Scenario();
        File.WriteAllText(Path.Combine(scenario.Worktree, "PendingChange.cs"), "// Unreviewed worker output.");
        Assert.True(scenario.Recovery().WorktreeDirty == true, "The control must observe a dirty goal worktree.");

        var proposal = Assert.Single(scenario.Supervisor().Proposals, candidate => candidate.TaskId is null);

        Assert.Equal(GoalSupervisorProposalKind.ParkForOperator, proposal.Kind);
        Assert.True(proposal.RequiresOperatorGate);
        Assert.StartsWith("Goal worktree is dirty", proposal.Reason);
        scenario.AssertInboxBlocker(proposal);
        Assert.Contains(scenario.Drain().Items, item => item.Stage == "operator-gate" &&
            item.RequiresOperatorGate && item.Detail == proposal.Reason);
    }

    [Fact]
    public void RepeatedDispatchFailureWithoutAlternateStillGates()
    {
        using var scenario = new Scenario();
        var task = scenario.Goal.Tasks.Single();
        const string stall = "Background dispatch made no observable progress before the stall timeout; wrapper heartbeat state=running.";
        scenario.RecordFailure(stall);
        scenario.Kernel.RetryTask(scenario.Goal.Id, task.Id, "Retry after repairing the worker.", RetryCause.ProviderInterruption);
        scenario.Clock.UtcNow = scenario.Clock.UtcNow.AddHours(3);
        scenario.RecordFailure(stall);
        Assert.Equal(2, task.VerificationHistory.Count);

        var proposals = scenario.Supervisor().Proposals.Where(candidate => candidate.TaskId == task.Id).ToArray();

        Assert.NotEmpty(proposals);
        Assert.All(proposals, proposal =>
        {
            Assert.Equal(GoalSupervisorProposalKind.ReDelegateAfterRecoverableFailure, proposal.Kind);
            Assert.True(proposal.RequiresOperatorGate);
            Assert.False(proposal.CanApply);
            scenario.AssertInboxBlocker(proposal);
        });
    }

    [Fact]
    public void CompletedTaskWithoutVerificationStillGates()
    {
        using var scenario = new Scenario();
        var task = scenario.Goal.Tasks.Single();
        scenario.Kernel.ReportTaskProgress(scenario.Goal.Id, task.Id, WorkTaskStatus.Completed, "Worker claimed completion.");
        Assert.Null(task.LastVerification);

        var proposal = Assert.Single(scenario.Supervisor().Proposals, candidate => candidate.TaskId == task.Id);

        Assert.Equal(GoalSupervisorProposalKind.VerifyMissingEvidence, proposal.Kind);
        Assert.True(proposal.RequiresOperatorGate);
        scenario.AssertInboxBlocker(proposal);
    }

    private sealed class Scenario : IDisposable
    {
        internal readonly string Root = LandingExecutorTests.CreateGitRepository();
        internal readonly FixtureClock Clock = new();
        internal readonly AgentOrchestratorKernel Kernel;
        internal readonly Goal Goal;
        internal readonly OrchestratorWorkspace Workspace;
        internal readonly string Worktree;
        private readonly WorkerProfileCatalog profiles = WorkerProfileCatalog.Default();
        private readonly IReadOnlyList<AgentDefinition> agents =
        [
            new(new AgentId("planner"), "Planner", AgentRole.Planner,
                new ModelProfile("OpenAI", "gpt-6.1-sol", ModelCapability.Text, SubscriptionMode.ApiKey),
                ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly,
                Subscription: new SubscriptionLaunchProfile("codex-cli"))
        ];

        internal Scenario(int taskCount = 1)
        {
            Workspace = OrchestratorWorkspace.ForDirectory(Root);
            Kernel = new AgentOrchestratorKernel(Clock);
            Goal = MarkGoalRefined(Kernel, Kernel.CreateGoal("Inspect supervisor proposals.",
                Enumerable.Range(1, taskCount).Select(index =>
                    new TaskSpec(TaskId.New(), $"Inspect scope {index}.", AgentRole.Planner)).ToArray()));
            Kernel.ActivateGoal(Goal.Id, agents);
            Worktree = GoalWorktrees.Ensure(Root, Goal.Id);
            Assert.All(Goal.Tasks, task =>
            {
                Assert.Equal(WorkTaskStatus.Assigned, task.Status);
                Assert.Null(task.LastDispatch);
                Assert.Empty(task.DispatchHistory);
                Assert.Null(task.LastVerification);
            });
            Assert.True(Recovery().WorktreeDirty == false, "The baseline must observe a clean goal worktree.");
        }

        internal GoalRecoveryReport Recovery() =>
            GoalRecoveryPlanner.Build(Kernel, Goal, Root, Workspace.IntegrationBranch, includeCleanupBackoff: false);

        internal GoalSupervisorPlan Supervisor() =>
            GoalSupervisor.Build(Kernel, Goal, agents, Root, Workspace.IntegrationBranch, AutonomyPolicy.SafeAuto);

        internal OperatorInboxReport Inbox() =>
            OperatorInbox.Build(Kernel, agents, profiles, Workspace, Goal.Id.Value[..8]);

        internal GoalDrainPlan Drain() =>
            GoalDrainPlanner.Build(Kernel, agents, profiles, Workspace, AutonomyPolicy.SafeAuto, apply: false);

        internal void AssertInboxBlocker(GoalSupervisorProposal proposal) =>
            Assert.Contains(Inbox().Items, item => item.Kind == OperatorInboxKind.SupervisorProposal &&
                item.TaskId == proposal.TaskId?.Value && item.Severity == OperatorInboxSeverity.Blocker &&
                item.Message == proposal.Reason && item.SuggestedCommand == proposal.SuggestedCommand);

        internal void RecordFailure(string error)
        {
            var task = Goal.Tasks.Single();
            var dispatch = new TaskDispatchRecord("codex-cli", "fixture worker", Worktree, Clock.UtcNow,
                ProviderName: "OpenAI", WorkerProviderKind: ProviderKind.OpenAICodexCli);
            Kernel.RecordTaskDispatch(Goal.Id, task.Id, dispatch);
            Kernel.RecordDispatchExecutionResult(Goal.Id, task.Id,
                new TaskVerificationRecord(dispatch.Command, Worktree, 1, "", error, Clock.UtcNow,
                    DispatchStartedAt: dispatch.DispatchedAt));
        }

        public void Dispose() => SharedTestSupport.RemoveTempDirectory(Root);
    }

    private sealed class FixtureClock : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = new(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
    }
}
