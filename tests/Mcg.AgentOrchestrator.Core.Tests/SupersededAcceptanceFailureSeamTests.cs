using System.Text.Json;
using Mcg.AgentOrchestrator.Core;

// Parallel-safe: each case owns an in-memory kernel; no processes or shared resources.
public sealed class SupersededAcceptanceFailureSeamTests
{
    [Theory]
    [InlineData(GoalStatus.Verified)]
    [InlineData(GoalStatus.Active)]
    [InlineData(GoalStatus.AcceptanceFailed)]
    public void Restore_ReadySupersededPair_PreservesTasksAndRecordsHistory(GoalStatus status)
    {
        var (kernel, goal, _) = ReadyGoal(status);
        var tasks = JsonSerializer.Serialize(goal.ToSnapshot().Tasks);

        Assert.True(kernel.RestoreVerifiedForSupersededAcceptanceFailure(goal.Id, "new-branch", "new-main"));

        Assert.Equal(GoalStatus.Verified, goal.Status);
        Assert.Null(goal.LatestAcceptanceFailure);
        Assert.Null(goal.RetainedAcceptanceFailure);
        Assert.Equal(tasks, JsonSerializer.Serialize(goal.ToSnapshot().Tasks));
        Assert.Equal(0, goal.AutomaticAcceptanceRetryCount);
        var history = Assert.Single(goal.Timeline.Where(evt => evt.Message.StartsWith(
            "Superseded acceptance failure restored Verified:", StringComparison.Ordinal)));
        Assert.Contains("recorded branch=old-branch main=old-main", history.Message);
        Assert.Contains("current branch=new-branch main=new-main", history.Message);
        Assert.False(kernel.RestoreVerifiedForSupersededAcceptanceFailure(goal.Id, "new-branch", "new-main"));
        var restored = AgentOrchestratorKernel.FromSnapshot(kernel.ExportSnapshot()).GetGoal(goal.Id);
        Assert.Null(restored.LatestAcceptanceFailure);
        Assert.Contains(history, restored.Timeline);
    }

    [Theory]
    [InlineData(GoalStatus.Parked)]
    [InlineData(GoalStatus.Verifying)]
    [InlineData(GoalStatus.WaitingForHuman)]
    [InlineData(GoalStatus.Completed)]
    [InlineData(GoalStatus.Failed)]
    public void Restore_IneligibleStatus_LeavesFailureAndTasksUntouched(GoalStatus status)
    {
        var (kernel, goal, _) = ReadyGoal(status);
        var before = JsonSerializer.Serialize(goal.ToSnapshot());

        Assert.False(kernel.RestoreVerifiedForSupersededAcceptanceFailure(goal.Id, "new-branch", "new-main"));

        Assert.Equal(before, JsonSerializer.Serialize(goal.ToSnapshot()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Restore_UnreadyTasks_LeavesFailureAndTasksUntouched(bool completedWithoutPass)
    {
        var (kernel, goal, task) = ReadyGoal(GoalStatus.Active);
        if (completedWithoutPass)
            kernel.RecordTaskVerification(goal.Id, task.Id,
                new("failed verification", "C:\\repo", 1, "red", "", DateTimeOffset.UnixEpoch));
        else
            task.SetStatus(WorkTaskStatus.Assigned);
        var before = JsonSerializer.Serialize(goal.ToSnapshot());

        Assert.False(kernel.RestoreVerifiedForSupersededAcceptanceFailure(goal.Id, "new-branch", "new-main"));

        Assert.Equal(before, JsonSerializer.Serialize(goal.ToSnapshot()));
    }

    [Theory]
    [InlineData("OLD-BRANCH", "OLD-MAIN")]
    [InlineData(null, "new-main")]
    [InlineData("new-branch", " ")]
    public void Restore_CurrentOrUnknownPair_RetainsFailure(string? branch, string? main)
    {
        var (kernel, goal, _) = ReadyGoal(GoalStatus.Verified);
        var before = JsonSerializer.Serialize(goal.ToSnapshot());

        Assert.False(kernel.RestoreVerifiedForSupersededAcceptanceFailure(goal.Id, branch, main));

        Assert.Equal(before, JsonSerializer.Serialize(goal.ToSnapshot()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Route_ConsumptionPredicate_ControlsRecordedFailureTransition(bool allow)
    {
        var (kernel, goal, _) = ReadyGoal(GoalStatus.Verified);
        var failure = goal.LatestAcceptanceFailure;
        var count = goal.Timeline.Count;
        var calls = 0;

        var routed = kernel.RouteRecordedAcceptanceFailure(goal.Id, "Pair checked", candidate =>
        {
            calls++;
            Assert.Same(failure, candidate);
            return allow;
        });

        Assert.Equal(1, calls);
        Assert.Equal(allow, routed);
        Assert.Equal(allow ? GoalStatus.AcceptanceFailed : GoalStatus.Verified, goal.Status);
        Assert.Same(failure, goal.LatestAcceptanceFailure);
        if (!allow) Assert.Equal(count, goal.Timeline.Count);
    }

    private static (AgentOrchestratorKernel Kernel, Goal Goal, TaskSpec Task) ReadyGoal(GoalStatus status)
    {
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Implement fix", AgentRole.Developer);
        var goal = kernel.CreateGoal("Pair-bound failure", [task]);
        kernel.ActivateGoal(goal.Id, DefaultAgents());
        kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Completed, "Done");
        kernel.RecordTaskVerification(goal.Id, task.Id,
            new("verification", "C:\\repo", 0, "passed", "", DateTimeOffset.UnixEpoch));
        kernel.RecordAcceptanceFailure(goal.Id, ["old red"], "old-branch", "old-main", attemptId: "old-attempt");
        if (status == GoalStatus.Parked) kernel.ParkGoal(goal.Id, "Parked");
        else goal.SetStatus(status);
        Assert.Equal(status, goal.Status);
        return (kernel, goal, task);
    }
}
