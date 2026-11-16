using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using static AttributedFailureDeveloperRouteTests;
using static ConductorDriverTests;

// Parallel-safe: the kernel, revision inputs and dispatch boundary are in-memory and test-owned.
public sealed class AcceptanceFailureActiveRepairDispatchTests
{
    [Theory(Timeout = TestHangGuard.WholeTestMilliseconds)]
    [InlineData(AgentRole.Reviewer, false)]
    [InlineData(AgentRole.Reviewer, true)]
    [InlineData(AgentRole.Tester, false)]
    [InlineData(AgentRole.Tester, true)]
    public void RetainedFailure_AfterDeveloperRepair_DispatchesDownstream(AgentRole role, bool missingRevision)
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = CompletedGoal(kernel);
        var downstream = kernel.AddTask(goal.Id, role, "Verify repaired source", DefaultAgents());
        PassVerification(kernel, goal, downstream);
        Assert.Equal(GoalStatus.Verified, goal.Status);
        kernel.RecordAcceptanceFailure(goal.Id, ["recorded red"], Candidate, Main);
        var failure = goal.LatestAcceptanceFailure;
        Assert.NotNull(failure);

        var developer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
        kernel.RetryTask(goal.Id, developer.Id, "Repair current source", RetryCause.NewSourceFinding);
        Assert.Equal(GoalStatus.Active, goal.Status);
        Assert.Same(failure, goal.LatestAcceptanceFailure);
        var currentBranch = new string('f', 40);
        DispatchTask(kernel, goal, developer, baseCommit: Candidate);
        kernel.RecordDispatchResultCommit(goal.Id, developer.Id, currentBranch);
        kernel.RecordTaskVerification(goal.Id, developer.Id,
            new("repair", "C:\\tmp", 0, "Source repaired", "", DateTimeOffset.UtcNow, HasCommittedChanges: true));
        Assert.Equal(GoalStatus.Active, goal.Status);
        Assert.Equal(WorkTaskStatus.Assigned, downstream.Status);
        Assert.Equal(currentBranch, developer.LastDispatch!.ResultCommit);
        Assert.Same(failure, goal.LatestAcceptanceFailure);
        var retryCount = goal.AutomaticAcceptanceRetryCount;
        var retries = goal.Timeline.Count(evt => evt.Kind == ProgressKind.TaskRetried);
        var starts = new List<TaskId>();
        var holds = new List<string>();
        var gates = 0;
        var driver = MakeDriver(
            getFacts: _ => new(WorkspaceExists: true),
            getPreReviewEvidenceContext: _ => NoPreReviewContext(currentBranch),
            dispatchAndStart: current =>
            {
                var ready = Assert.Single(current.Tasks, task => task.Status == WorkTaskStatus.Assigned);
                Assert.Equal(downstream.Id, ready.Id);
                starts.Add(ready.Id);
                DispatchTask(kernel, current, ready);
                return DispatchStartOutcome.Started();
            },
            runAcceptanceSummary: _ =>
            {
                gates++;
                return AcceptanceVerificationSummary.PassedWithNoUnmetCriteria;
            },
            writeEscalation: (_, _, reason) => holds.Add(reason));
        driver.CohortAttributionRevisionReader = _ => new(currentBranch, missingRevision ? null : Main);
        driver.BeginTick(kernel, 1);

        var executed = Assert.IsType<ConductorAdvanceOutcome.Executed>(
            driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive).Outcome);

        Assert.Equal(GoalLifecycleState.WorkspaceReady, executed.FromState);
        Assert.Equal(downstream.Id, Assert.Single(starts));
        Assert.Empty(holds);
        Assert.Equal(0, gates);
        Assert.Same(failure, goal.LatestAcceptanceFailure);
        Assert.Equal(retryCount, goal.AutomaticAcceptanceRetryCount);
        Assert.Equal(retries, goal.Timeline.Count(evt => evt.Kind == ProgressKind.TaskRetried));
        Assert.DoesNotContain(goal.Timeline, evt => evt.Message.StartsWith(
            "Superseded acceptance failure restored Verified:", StringComparison.Ordinal));
    }
}
