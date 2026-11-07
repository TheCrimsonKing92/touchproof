using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed class MultiRepositoryLandingHoldAttention(ICollaborationItemStore store, Func<Goal, bool>? hasCurrentPass = null)
{
    internal const string Prefix = "multi-repository-landing:";

    internal void ReconcileAll(IEnumerable<Goal> goals)
    {
        foreach (var goal in goals) Reconcile(goal);
    }

    internal void Reconcile(Goal goal)
    {
        if (!MultiRepositoryLandingHold.Applies(goal)) return;
        // Resolve attention left by the previous slice; it no longer describes the required action.
        store.TryResolveAsync("multi-repository-gate:" + goal.Id.Value, "Multi-repository acceptance is available.").GetAwaiter().GetResult();
        var key = Prefix + goal.Id.Value;
        if (GoalLifecycle.ResolveState(goal) is GoalLifecycleState.Verified or GoalLifecycleState.Verifying
            && hasCurrentPass?.Invoke(goal) == true)
            store.RaiseAsync(CollaborationItemType.Decision, goal.Id.Value, MultiRepositoryLandingHold.Kind,
                "Owner: operator. " + MultiRepositoryLandingHold.NextAction + ".", key).GetAwaiter().GetResult();
        else
            store.TryResolveAsync(key, "Multi-repository landing wait cleared.").GetAwaiter().GetResult();
    }
}
