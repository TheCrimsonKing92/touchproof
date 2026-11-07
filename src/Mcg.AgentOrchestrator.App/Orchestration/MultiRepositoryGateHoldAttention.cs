using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed class MultiRepositoryGateHoldAttention(ICollaborationItemStore store)
{
    internal const string Prefix = "multi-repository-gate:";

    internal void ReconcileAll(IEnumerable<Goal> goals)
    {
        foreach (var goal in goals) Reconcile(goal);
    }

    internal void Reconcile(Goal goal)
    {
        var key = Prefix + goal.Id.Value;
        if (MultiRepositoryGateHold.Applies(goal) &&
            GoalLifecycle.ResolveState(goal) is GoalLifecycleState.Verified or GoalLifecycleState.Verifying)
            store.RaiseAsync(CollaborationItemType.Decision, goal.Id.Value, MultiRepositoryGateHold.Kind,
                "Owner: operator. " + MultiRepositoryGateHold.NextAction + ".", key).GetAwaiter().GetResult();
        else
            store.TryResolveAsync(key, "Multi-repository gate wait cleared.").GetAwaiter().GetResult();
    }
}
