namespace Mcg.AgentOrchestrator.Core;

public static class LoggedGoalPolicyDecisionRestorer
{
    // Re-attach an already-logged decision after a checkpoint rebase, preserving its identity.
    // The original writer owns event-log emission; this operation must not log a duplicate.
    public static void Restore(AgentOrchestratorKernel kernel, ProgressEvent decision)
    {
        ArgumentNullException.ThrowIfNull(kernel);
        ArgumentNullException.ThrowIfNull(decision);
        if (decision.Kind != ProgressKind.GoalPolicyDecision || decision.TaskId is not null)
            throw new ArgumentException("Only goal policy decisions can be restored.", nameof(decision));
        var goal = kernel.GetGoal(decision.GoalId);
        if (!goal.Timeline.Any(entry => entry.Kind == decision.Kind &&
            entry.OccurredAt == decision.OccurredAt && entry.Message == decision.Message && entry.TaskId is null))
            goal.Append(decision);
    }
}
