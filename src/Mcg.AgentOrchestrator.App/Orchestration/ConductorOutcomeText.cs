using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class ConductorOutcomeText
{
    internal const string PendingBlockedIntentClause = "; pending AppliedBlocked intent ";

    internal static string Format(ConductorAdvanceOutcome outcome) => outcome switch
    {
        ConductorAdvanceOutcome.Executed e  => $"executed from {e.FromState} — {e.Description}",
        ConductorAdvanceOutcome.Held h      => $"held at {h.State} — {h.Reason}",
        ConductorAdvanceOutcome.Escalated e => $"escalated at {e.State} — {e.Reason}",
        ConductorAdvanceOutcome.Done d      => $"done ({d.State})",
        _                                   => outcome.ToString()!
    };

    internal static string BuildTerminalEscalationReason(
        Goal goal, GoalLifecycleState state, Func<string, string> trim)
    {
        var latestTaskFailure = LatestTaskFailure(goal, state);
        var reason = latestTaskFailure is null
            ? $"Goal is in {state} state; operator action required"
            : $"Goal is in {state} state; TaskFailed: {trim(latestTaskFailure.Message)}; operator action required";
        if (state == GoalLifecycleState.Failed &&
            goal.Status is not (GoalStatus.Cancelled or GoalStatus.Superseded))
        {
            foreach (var applied in OperatorIntentEffectCheck.PendingBlocked(goal))
                reason += $"{PendingBlockedIntentClause}{applied.IntentId} ({applied.Verb} task {applied.TaskId}); " +
                    OperatorIntentEffectCheck.Describe(applied.BlockedBy!);
        }
        return reason;
    }

    internal static ProgressFailurePayload? TerminalFailure(Goal goal, GoalLifecycleState state) =>
        LatestTaskFailure(goal, state) is { } failure
            ? failure.Failure ?? new(failure.Kind.ToString(), null, []) : null;

    internal static ProgressEvent? LatestTaskFailure(Goal goal, GoalLifecycleState state)
    {
        var failedTaskIds = goal.Tasks
            .Where(task => task.Status == WorkTaskStatus.Failed)
            .Select(task => task.Id)
            .ToHashSet();
        return state == GoalLifecycleState.Failed &&
               goal.Status is not (GoalStatus.Cancelled or GoalStatus.Superseded)
            ? goal.Timeline.LastOrDefault(item =>
                item.Kind == ProgressKind.TaskFailed &&
                item.TaskId is { } taskId &&
                failedTaskIds.Contains(taskId))
            : null;
    }
}
