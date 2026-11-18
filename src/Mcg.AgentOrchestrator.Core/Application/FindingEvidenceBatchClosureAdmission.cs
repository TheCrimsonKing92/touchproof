namespace Mcg.AgentOrchestrator.Core;

public static class FindingEvidenceBatchClosureAdmission
{
    public static bool TryApply(Goal goal, FindingEvidenceBatchClosure closure,
        Func<bool> isCurrent, Func<bool> apply)
    {
        lock (goal)
        {
            var marker = $"finding-evidence batch-closed; batch_key={closure.Identity.Key};";
            if (goal.Timeline.Any(item => item.TaskId == closure.Identity.RequestingTaskId &&
                    item.Kind == ProgressKind.FindingEvidenceRequestRecorded &&
                    item.Message.StartsWith(marker, StringComparison.Ordinal)) || !isCurrent()) return false;
            var message = marker + " " + closure.Accounting;
            // Stage accounting in the authoritative goal snapshot before the effect can
            // clear the evidence round. The next checkpoint persists both together.
            var progress = new ProgressEvent(goal.Id, closure.Identity.RequestingTaskId,
                ProgressKind.FindingEvidenceRequestRecorded, message, DateTimeOffset.UtcNow);
            goal.Append(progress);
            var applied = false;
            try
            {
                return applied = apply();
            }
            finally
            {
                if (!applied) goal.RemoveStagedProgress(progress);
            }
        }
    }
}
