using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class GoalWorkerDirectory
{
    public static string Resolve(OrchestratorWorkspace workspace, GoalId goalId)
    {
        var worktreePath = GoalWorktrees.TryResolve(workspace.ExecutionDirectory, goalId);
        if (worktreePath is not null)
            return worktreePath;

        if (GoalWorktrees.IsGitWorkTree(workspace.ExecutionDirectory))
            throw new MissingGoalWorktreeException(
                goalId, GoalWorktrees.WorktreePath(workspace.ExecutionDirectory, goalId));

        return workspace.ExecutionDirectory;
    }
}
