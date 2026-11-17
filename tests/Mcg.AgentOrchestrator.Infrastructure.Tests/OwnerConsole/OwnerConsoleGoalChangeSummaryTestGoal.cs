using Mcg.AgentOrchestrator.Core;

internal sealed class OwnerConsoleGoalChangeSummaryTestGoal(GoalSnapshot snapshot)
{
    private readonly Goal _goal = AgentOrchestratorKernel.FromSnapshot(new([snapshot], [])).Goals.Single();
    internal GoalId Id => _goal.Id;
    internal string Objective => _goal.Objective;
    internal GoalSnapshot ToSnapshot() => snapshot;
    public static implicit operator Goal(OwnerConsoleGoalChangeSummaryTestGoal fixture) => fixture._goal;
}
