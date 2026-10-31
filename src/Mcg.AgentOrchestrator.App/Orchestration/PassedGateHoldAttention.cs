using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed class PassedGateHoldAttention(ICollaborationItemStore store, Func<Goal, PassedGateHold?> resolve)
{
    internal const string Prefix = "passed-gate-hold:";

    internal void ReconcileAll(IEnumerable<Goal> goals)
    {
        var open = store.GetAttentionQueueAsync().GetAwaiter().GetResult()
            .Where(item => !CollaborationItemLifecycle.IsTerminal(item.Status) &&
                item.CorrelationKey?.StartsWith(Prefix, StringComparison.Ordinal) == true)
            .Select(item => item.GoalId).ToHashSet(StringComparer.Ordinal);
        foreach (var goal in goals.Where(goal => goal.Status == GoalStatus.Verified && goal.LandingBlocker is not null ||
            open.Contains(goal.Id.Value)))
            PassedGateHoldFaultIsolation.Run(goal, "reconcile-attention", () => Reconcile(goal));
    }

    internal void Reconcile(Goal goal) => Reconcile(store, goal, resolve(goal));

    internal static void Reconcile(ICollaborationItemStore store, Goal goal, PassedGateHold? hold)
    {
        var key = Prefix + goal.Id.Value;
        if (hold is null)
            store.TryResolveAsync(key, "Passed gate landing hold cleared.").GetAwaiter().GetResult();
        else
            store.RaiseAsync(CollaborationItemType.Decision, goal.Id.Value,
                $"passed gate, held: {hold.CategoryText}", hold.Describe(), key).GetAwaiter().GetResult();
    }
}
