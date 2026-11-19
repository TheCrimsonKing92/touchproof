namespace Mcg.AgentOrchestrator.Core;

public static class FindingEvidenceCompletionPublication
{
    public static void Apply(Goal goal, TaskSpec task, Action publish, Action? checkpoint)
    {
        lock (goal)
        {
            var verification = task.LastVerification ??
                throw new InvalidOperationException("Finding evidence publication requires an existing verification.");
            var timelineCount = goal.Timeline.Count;
            try
            {
                publish();
                checkpoint?.Invoke();
            }
            catch
            {
                // A partial receipt could regroup the requests before the original attempt is replayed.
                task.RestoreFindingEvidenceVerification(verification);
                foreach (var entry in goal.Timeline.Skip(timelineCount).Where(entry => entry.TaskId == task.Id &&
                             entry.Kind is ProgressKind.FindingEvidenceRequestRecorded or ProgressKind.FindingEvidenceRunRecorded))
                    goal.RemoveStagedProgress(entry);
                throw;
            }
        }
    }
}
