using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed class MissingGoalWorktreeException : InvalidOperationException
{
    public MissingGoalWorktreeException(GoalId goalId, string expectedWorktreePath)
        : base($"Goal {goalId.Value} is missing its worktree at '{expectedWorktreePath}'.")
    {
        GoalId = goalId;
        ExpectedWorktreePath = expectedWorktreePath;
    }

    public GoalId GoalId { get; }
    public string ExpectedWorktreePath { get; }
}
