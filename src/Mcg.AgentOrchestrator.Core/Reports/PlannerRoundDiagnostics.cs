namespace Mcg.AgentOrchestrator.Core;

/// <summary>Read-time evidence for a Planner dispatch; null means linkage was not observed reliably.</summary>
internal static class PlannerRoundDiagnostics
{
    internal static Dictionary<DateTimeOffset, (bool? Rejected, bool? Clarification)> FromTask(Goal goal, TaskSpec task)
    {
        if (task.RequiredRole != AgentRole.Planner) return [];

        var windows = task.DispatchHistory.GroupBy(d => d.DispatchedAt).OrderBy(g => g.Key)
            .Select(g => new Window(g.Key, g.Distinct().Count() > 1)).ToArray();
        if (windows.Length == 0) return [];
        var byStart = windows.ToDictionary(w => w.Start);
        var starts = windows.Select(w => w.Start).ToArray();
        var verifications = task.VerificationHistory
            .Concat(task.LastVerification is { } last ? [last] : [])
            .Where(v => v.DispatchAttemptAt.HasValue).GroupBy(v => v.CompletedAt)
            .ToDictionary(g => g.Key, g => g.Select(v => v.DispatchAttemptAt!.Value).Distinct().ToArray());

        foreach (var evt in goal.Timeline.Where(e => e.GoalId == goal.Id && e.TaskId == task.Id))
        {
            // Only the host's persisted classifier channel and registered task request events qualify.
            // Never search verification stdout, worker narration, operator notes or arbitrary substrings.
            var rule = evt.Kind == ProgressKind.TaskNote && evt.Message.StartsWith("CLASSIFIER rule=", StringComparison.Ordinal)
                ? TaskOutcomeClassifier.TryExtractRule(evt.Message)
                : evt.Kind == ProgressKind.TaskFailed ? evt.Failure?.Rule : null;
            var request = evt.Kind == ProgressKind.HumanInputRequested;
            if (rule is null && !request) continue;

            var position = Array.BinarySearch(starts, evt.OccurredAt);
            if (position < 0) position = ~position - 1;
            var window = position >= 0 ? windows[position] : null;
            if (verifications.TryGetValue(evt.OccurredAt, out var identities) &&
                (identities.Length != 1 || window is null || identities[0] != window.Start))
            {
                // A delayed/conflicting claim cannot migrate to a later dispatch or prove absence
                // in its claimed dispatch. Invalidate only the affected measurement channel.
                window?.Invalidate(request);
                foreach (var identity in identities)
                    if (byStart.TryGetValue(identity, out var claimed)) claimed.Invalidate(request);
                continue;
            }
            if (window is null) continue;
            if (request) window.Clarification = true;
            else
            {
                window.Rejected |= string.Equals(rule, TaskOutcomeRules.PlannerOutputContractRejected.Token,
                    StringComparison.Ordinal);
                window.ClassifierObserved = true;
            }
        }

        return windows.ToDictionary(w => w.Start, w => (
            w.Ambiguous || w.RejectionConflict ? null : w.ClassifierObserved ? (bool?)w.Rejected : null,
            w.Ambiguous || w.ClarificationConflict ? null : w.Clarification ? (bool?)true : w.ClassifierObserved ? (bool?)false : null));
    }

    private sealed class Window(DateTimeOffset start, bool ambiguous)
    {
        internal DateTimeOffset Start { get; } = start;
        internal bool Ambiguous { get; } = ambiguous;
        internal bool ClassifierObserved { get; set; }
        internal bool Rejected { get; set; }
        internal bool Clarification { get; set; }
        internal bool RejectionConflict { get; private set; }
        internal bool ClarificationConflict { get; private set; }
        internal void Invalidate(bool request)
        {
            if (request) ClarificationConflict = true;
            else RejectionConflict = true;
        }
    }
}
