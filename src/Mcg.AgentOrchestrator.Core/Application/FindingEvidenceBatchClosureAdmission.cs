namespace Mcg.AgentOrchestrator.Core;

public static class FindingEvidenceBatchClosureAdmission
{
    public static bool TryApply(Goal goal, FindingEvidenceBatchClosure closure,
        Func<bool> isCurrent, Func<bool> apply, Action<string> record)
    {
        lock (goal)
        {
            var marker = $"finding-evidence batch-closed; batch_key={closure.Identity.Key};";
            if (goal.Timeline.Any(item => item.TaskId == closure.Identity.RequestingTaskId &&
                    item.Kind == ProgressKind.FindingEvidenceRequestRecorded &&
                    item.Message.StartsWith(marker, StringComparison.Ordinal)) || !isCurrent()) return false;
            if (!apply()) return false;
            record(marker + " " + closure.Accounting);
            return true;
        }
    }
}
