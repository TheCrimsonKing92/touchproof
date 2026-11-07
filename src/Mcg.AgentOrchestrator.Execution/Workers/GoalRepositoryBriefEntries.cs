using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

public static class GoalRepositoryBriefEntries
{
    public static IReadOnlyDictionary<string, string>? For(Goal goal, string homeWorktree) =>
        goal.Repositories?.Keys.ToDictionary(key => key, key => key == "default" ? homeWorktree :
            GoalRepositoryWorktreeLayout.WorktreePath(goal.Repositories.CheckoutRoots[key], goal.Id, key),
            StringComparer.Ordinal);
}
