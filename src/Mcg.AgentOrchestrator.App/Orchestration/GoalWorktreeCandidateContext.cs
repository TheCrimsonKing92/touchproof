using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class GoalWorktreeCandidateContext
{
    internal static Func<Goal, (CandidateIdentity? Identity, string Failure)> Configure(
        AgentOrchestratorKernel kernel, OrchestratorWorkspace workspace)
    {
        kernel.SetCandidatePatchEquivalence(new GoalWorktreeCandidatePatchEquivalence(
            workspace.ExecutionDirectory, workspace.IntegrationBranch));
        return goal =>
        {
            var path = GoalWorktrees.TryResolve(workspace.ExecutionDirectory, goal.Id);
            if (path is null) return (null, "missing-worktree");
            GoalWorktrees.TryComputeCandidateIdentity(path, workspace.IntegrationBranch, out var identity, out var failure,
                projectHomeDirectory: workspace.ProjectHomeDirectoryOrNull);
            return (identity, failure);
        };
    }
}
