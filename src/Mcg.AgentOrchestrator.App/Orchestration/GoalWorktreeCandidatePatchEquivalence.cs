using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed class GoalWorktreeCandidatePatchEquivalence(
    string executionDirectory, string integrationBranch) : ICandidatePatchEquivalence
{
    public bool AreEquivalent(GoalId goal, string reviewedSha, string currentSha, out string evidence)
    {
        evidence = string.Empty;
        try
        {
            var worktree = GoalWorktrees.TryResolve(executionDirectory, goal);
            return worktree is not null && GoalWorktrees.TryComputePatchEquivalence(
                worktree, reviewedSha, currentSha, integrationBranch, out evidence, out _);
        }
        catch (Exception)
        {
            // Context lookup and git failures must retain normal downstream invalidation.
            evidence = string.Empty;
            return false;
        }
    }
}
