using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class TerminalGoalEvidenceAging
{
    internal static DateTimeOffset? ResolveTerminalAt(
        GoalStatus status, IEnumerable<DateTimeOffset> eventTimes, DateTimeOffset? recordedTerminalAt = null) =>
        StorageRetentionMaintenance.IsReclaimableTerminalStatus(status)
            ? recordedTerminalAt ?? eventTimes.Select(time => (DateTimeOffset?)time).Max()
            : null;

    internal static bool IsPastTerminalWindow(
        StorageRetentionGoal goal,
        IReadOnlyCollection<RetentionAttemptIdentity> attempts,
        DateTimeOffset now,
        StorageRetentionReclaimOptions options)
    {
        if (goal.IsStoreStandIn || !StorageRetentionMaintenance.IsReclaimableTerminalStatus(goal.Status) ||
            goal.TerminalAt is not { } terminalAt || options.TerminalGoalAttemptMaxAge is not { } maxAge)
            return false;

        var window = StorageRetentionMaintenance.MaxAttemptGrace(maxAge);
        // A newer attempt makes an older timeline timestamp insufficient evidence of terminal age.
        return now - terminalAt > window && attempts.All(attempt => now - attempt.StartedAt > window);
    }
}
