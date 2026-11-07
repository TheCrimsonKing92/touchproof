using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class GoalRepositoryDispatchProvisioning
{
    internal static bool TryProvision(Goal goal, TaskSpec task, out string failure)
    {
        failure = string.Empty;
        if (goal.Repositories is not { } declaration ||
            DispatchRoleOutputCapabilities.TryGet(task.RequiredRole, out var capability) &&
            capability == DispatchRoleOutputCapability.ReadOnly) return true;
        var roots = declaration.CheckoutRoots.Where(entry => entry.Key != "default")
            .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);
        return GoalRepositoryWorktreeProvisioner.TryProvision(new GoalRepositorySet(roots.Keys),
            goal.Id, roots, out _, out failure);
    }
}
