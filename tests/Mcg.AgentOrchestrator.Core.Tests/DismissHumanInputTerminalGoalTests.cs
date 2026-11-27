using System.Text.Json;
using Mcg.AgentOrchestrator.Core;

// Parallel-safe: each case owns its kernel and requests.
public sealed class DismissHumanInputTerminalGoalTests
{
    [Theory]
    [InlineData(GoalStatus.Cancelled)]
    [InlineData(GoalStatus.Completed)]
    [InlineData(GoalStatus.Superseded)]
    public void TaskLinkedDismissal_PreservesTerminalGoalAndTasks(GoalStatus status)
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = CreateActiveGoal(kernel);
        var (request, answered) = CreateRequests(kernel, goal);
        var snapshot = kernel.ExportSnapshot();
        var terminalGoal = snapshot.Goals.Single() with { Status = status };
        var terminal = AgentOrchestratorKernel.FromSnapshot(snapshot with { Goals = [terminalGoal] });
        var before = terminal.ExportSnapshot().Goals.Single();

        terminal.DismissHumanInput(request.Id);

        Assert.True(terminal.HumanInputRequests.Single(wait => wait.Id == request.Id).WasDismissed);
        Assert.Equal("Original answer", terminal.HumanInputRequests.Single(wait => wait.Id == answered.Id).Answer);
        var after = terminal.ExportSnapshot().Goals.Single();
        Assert.Equal(status, after.Status);
        Assert.Equal(JsonSerializer.Serialize(before.Tasks), JsonSerializer.Serialize(after.Tasks));
        Assert.Equal(JsonSerializer.Serialize(before.Timeline), JsonSerializer.Serialize(after.Timeline.Take(before.Timeline.Count)));
        Assert.StartsWith("Dismissed human wait", Assert.Single(after.Timeline.Skip(before.Timeline.Count)).Message);
    }

    [Fact]
    public void TaskLinkedDismissal_RestoresRunningOnActiveGoal()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = CreateActiveGoal(kernel);
        var (request, _) = CreateRequests(kernel, goal);
        var snapshot = kernel.ExportSnapshot();
        kernel = AgentOrchestratorKernel.FromSnapshot(snapshot with
        {
            Goals = [snapshot.Goals.Single() with { Status = GoalStatus.Active }]
        });
        var task = kernel.GetGoal(goal.Id).Tasks.Single();
        Assert.Equal(WorkTaskStatus.WaitingForHuman, task.Status);

        kernel.DismissHumanInput(request.Id);

        Assert.Equal(WorkTaskStatus.Running, task.Status);
        Assert.True(kernel.HumanInputRequests.Single(wait => wait.Id == request.Id).WasDismissed);
    }

    private static Goal CreateActiveGoal(AgentOrchestratorKernel kernel)
    {
        var goal = kernel.CreateGoal("Maintain task status on dismissal",
            [new TaskSpec(TaskId.New(), "Linked task", AgentRole.Developer)]);
        kernel.ActivateGoal(goal.Id, DefaultAgents());
        return goal;
    }

    private static (HumanInputRequest Request, HumanInputRequest Answered) CreateRequests(AgentOrchestratorKernel kernel, Goal goal)
    {
        var answered = kernel.RecordAnsweredOperatorHumanInput(goal.Id, "Earlier decision", "Original answer");
        var request = kernel.RequestHumanInput(goal.Id, goal.Tasks.Single().Id, "Dismiss this", isDismissible: true);
        return (request, answered);
    }
}
