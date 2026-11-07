using Mcg.AgentOrchestrator.App.Application;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: real git and SQLite resources are case-owned; process launch is intercepted.
public sealed class GoalRepositoriesDispatchProvisioningTests
{
    [Fact]
    public void StartDispatches_DeclaredWritableGoal_ProvisionsBeforeLaunch()
    {
        using var fixture = new GoalRepositoriesTestFixture();
        var goal = fixture.CreateGoal();
        var home = fixture.CreateHomeWorktree(goal);
        var task = goal.Tasks.Single();
        Prepare(fixture, goal);
        var reachedStart = false;
        var stop = new InvalidOperationException("fixture stopped at worker start");

        var error = Assert.Throws<InvalidOperationException>(() => new GoalDispatchOperations().StartDispatches(
            fixture.Kernel, fixture.Workspace, goal, fixture.Agents, fixture.Profiles,
            refreshBeforeStart: false, runner: new BackgroundDispatchRunner(disableProcessStart: false),
            sandboxOptions: GoalRepositoriesTestFixture.DisabledSandbox,
            checkpointBeforeWorkerStart: (_, _, _, phase) =>
            {
                Assert.Equal(DispatchRecordCheckpointPhase.BeforeProcessStart, phase);
                var secondary = fixture.Crosswalk.WorktreePath(goal.Id, "crosswalk");
                Assert.Equal(GoalWorktreeLayout.BranchName(goal.Id),
                    GoalRepositoryWorktreeProvisionerFixture.Run(home, "symbolic-ref", "--short", "HEAD"));
                Assert.Equal(GoalRepositoryWorktreeLayout.BranchName(goal.Id, "crosswalk"),
                    GoalRepositoryWorktreeProvisionerFixture.Run(secondary, "symbolic-ref", "--short", "HEAD"));
                var prompt = File.ReadAllText(task.LastDispatch!.PromptPath!);
                Assert.Contains("## Repositories", prompt);
                Assert.Contains("- default: " + home, prompt);
                Assert.Contains("- crosswalk: " + secondary, prompt);
                reachedStart = true;
                throw stop;
            }));

        Assert.Same(stop, error);
        Assert.True(reachedStart);
        Assert.Null(task.LastProcess);
        Assert.DoesNotContain("--repo-r-64656661756c74", fixture.Home.Branches());
    }

    [Fact]
    public void StartDispatches_SecondaryConflict_FailsWithoutRealFailureRetry()
    {
        using var fixture = new GoalRepositoriesTestFixture();
        var goal = fixture.CreateGoal();
        fixture.CreateHomeWorktree(goal);
        Prepare(fixture, goal);
        var task = goal.Tasks.Single();
        Directory.CreateDirectory(fixture.Crosswalk.WorktreePath(goal.Id, "crosswalk"));
        var retries = task.RetryAdmissionHistory.ToArray();
        var retryEvents = goal.Timeline.Count(entry => entry.Kind == ProgressKind.TaskRetried);

        var result = new GoalDispatchOperations().StartDispatches(fixture.Kernel, fixture.Workspace,
            goal, fixture.Agents, fixture.Profiles, refreshBeforeStart: false,
            runner: new BackgroundDispatchRunner(disableProcessStart: true),
            sandboxOptions: GoalRepositoriesTestFixture.DisabledSandbox);

        Assert.StartsWith("crosswalk:layout-conflict", Assert.Single(result.StartFailures!).Reason);
        Assert.Null(task.LastProcess);
        Assert.Equal(WorkTaskStatus.Running, task.Status);
        Assert.Equal(retries, task.RetryAdmissionHistory);
        Assert.Equal(retryEvents, goal.Timeline.Count(entry => entry.Kind == ProgressKind.TaskRetried));
    }

    [Fact]
    public void TryProvision_ReadOnlyRole_DoesNotCreateSecondaryWorktree()
    {
        using var fixture = new GoalRepositoriesTestFixture();
        var goal = fixture.CreateGoal(AgentRole.Reviewer);
        Assert.True(GoalRepositoryDispatchProvisioning.TryProvision(goal, goal.Tasks.Single(), out var reason), reason);
        fixture.Crosswalk.AssertAbsent(goal.Id, "crosswalk");
    }

    private static void Prepare(GoalRepositoriesTestFixture fixture, Goal goal)
    {
        var task = goal.Tasks.Single();
        var agent = fixture.Agents.Single(candidate => candidate.Id == task.AssignedAgentId);
        new GoalDispatchOperations().ProfileDispatchTask(fixture.Kernel, fixture.Workspace, goal, task,
            fixture.Profiles.GetRequired(agent.Subscription!.WorkerProfileName), fixture.Agents,
            sandboxOptions: GoalRepositoriesTestFixture.DisabledSandbox);
    }
}
