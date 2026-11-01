using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed class AwaitingVerificationGateAttention(ICollaborationItemStore store)
{
    internal const string Prefix = "awaiting-verification-gate:";

    internal void ReconcileAll(IEnumerable<Goal> goals)
    {
        var open = store.GetAttentionQueueAsync().GetAwaiter().GetResult()
            .Where(item => !CollaborationItemLifecycle.IsTerminal(item.Status) &&
                item.CorrelationKey?.StartsWith(Prefix, StringComparison.Ordinal) == true)
            .Select(item => item.GoalId).ToHashSet(StringComparer.Ordinal);
        // The supplied goals are the tick's authorized scope. Raise new waits only
        // from a held outcome; here refresh or clear existing waits in that scope.
        foreach (var goal in goals.Where(goal => open.Contains(goal.Id.Value)))
            PassedGateHoldFaultIsolation.Run(goal, "reconcile-awaiting-verification-attention",
                () => Reconcile(goal, GoalLifecycle.ResolveState(goal) == GoalLifecycleState.AwaitingVerification));
    }

    internal void Reconcile(Goal goal, bool held) => Reconcile(store, goal, held);

    internal static void Reconcile(ICollaborationItemStore store, Goal goal, bool held)
    {
        var body = held ? AwaitingVerificationHoldReasonBuilder.BuildOperatorWait(goal) : null;
        var key = Prefix + goal.Id.Value;
        if (body is null)
            store.TryResolveAsync(key, "AwaitingVerification task gate wait cleared.").GetAwaiter().GetResult();
        else
            store.RaiseAsync(CollaborationItemType.Decision, goal.Id.Value,
                "AwaitingVerification: operator task gates", body, key).GetAwaiter().GetResult();
    }
}
