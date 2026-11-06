using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class OperatorIntentEffectCheck
{
    internal static OperatorIntentBlocker? Evaluate(Goal goal, OperatorIntentRecord intent)
    {
        if (intent.TaskId is null || intent.Verb is not
            (OperatorIntentVerbs.Adjudicate or OperatorIntentVerbs.Progress or OperatorIntentVerbs.VerifyManual))
            return null;

        var state = GoalLifecycle.ResolveState(goal);
        if (state != GoalLifecycleState.Failed ||
            goal.Status is GoalStatus.Cancelled or GoalStatus.Superseded)
            return null;

        var failure = ConductorOutcomeText.LatestTaskFailure(goal, state);
        var blocker = failure?.TaskId is { } taskId
            ? goal.Tasks.First(task => task.Id == taskId)
            : goal.Tasks.FirstOrDefault(task => task.Status == WorkTaskStatus.Failed);
        return blocker is null ? null : Capture(goal, blocker);
    }

    private static OperatorIntentBlocker Capture(Goal goal, TaskSpec task)
    {
        var failures = goal.Timeline.Where(item => item.TaskId == task.Id &&
            item.Kind == ProgressKind.TaskFailed &&
            (task.LatestRetryAt is null || item.OccurredAt >= task.LatestRetryAt)).ToArray();
        var reason = failures.LastOrDefault(item => item.Failure is not null)?.Failure;
        return new(task.Id.Value, task.RequiredRole, reason?.Rule ?? reason?.Kind ?? "failure rule not recorded",
            goal.Tasks.ToList().IndexOf(task) + 1, failures.LastOrDefault()?.OccurredAt, task.LatestRetryAt);
    }

    internal static string Describe(OperatorIntentBlocker blocker) =>
        $"blocked by Failed {blocker.Role} task {blocker.TaskId} ({blocker.Reason}); " +
        $"close or route task {blocker.TaskNumber} to proceed";

    internal static IEnumerable<OperatorIntentAppliedPayload> PendingBlocked(Goal goal)
    {
        foreach (var item in goal.Timeline)
        {
            if (item.OperatorIntentApplied is not { BlockedBy: { } blocker } applied)
                continue;
            var task = goal.Tasks.FirstOrDefault(task => task.Id.Value == blocker.TaskId);
            if (task is { Status: WorkTaskStatus.Failed } && Capture(goal, task) == blocker)
                yield return applied;
        }
    }
}
