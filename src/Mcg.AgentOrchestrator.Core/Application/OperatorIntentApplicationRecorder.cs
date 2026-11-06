namespace Mcg.AgentOrchestrator.Core;

// Owns record-once intent application evidence in the goal timeline and lifecycle writer.
internal static class OperatorIntentApplicationRecorder
{
    internal static bool RecordApplied(
        Goal goal, OperatorIntentAppliedPayload payload, string message,
        IClock clock, IGoalLifecycleEventWriter eventWriter)
    {
        if (!string.IsNullOrEmpty(payload.IntentId) &&
            goal.Timeline.Any(evt => evt.OperatorIntentApplied?.IntentId == payload.IntentId))
        {
            return false;
        }

        var progressEvent = new ProgressEvent(
            goal.Id, null, ProgressKind.GoalPolicyDecision, message, clock.UtcNow,
            OperatorIntentApplied: payload);
        goal.Append(progressEvent);
        eventWriter.AppendTimelineEvent(progressEvent);
        return true;
    }

    internal static bool RecordBlocked(
        Goal goal, string intentId, OperatorIntentBlocker blocker, string detail,
        IClock clock, IGoalLifecycleEventWriter eventWriter)
    {
        if (goal.Timeline.Any(item => item.OperatorIntentApplied is { BlockedBy: not null } applied &&
                applied.IntentId == intentId))
            return false;

        var application = goal.Timeline.LastOrDefault(item =>
            item.OperatorIntentApplied is { } applied && applied.IntentId == intentId &&
            applied.Outcome != "rejected")?.OperatorIntentApplied
            ?? throw new InvalidOperationException("A blocked intent must have an applied intent record.");
        var progressEvent = new ProgressEvent(goal.Id, null, ProgressKind.GoalPolicyDecision,
            $"operator-intent:{intentId} outcome=AppliedBlocked; {detail}", clock.UtcNow,
            OperatorIntentApplied: application with { Outcome = "applied-blocked", BlockedBy = blocker });
        goal.Append(progressEvent);
        eventWriter.AppendTimelineEvent(progressEvent);
        return true;
    }
}
