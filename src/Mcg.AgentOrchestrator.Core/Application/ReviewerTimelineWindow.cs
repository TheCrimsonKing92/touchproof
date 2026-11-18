namespace Mcg.AgentOrchestrator.Core;

internal sealed record ReviewerTimelineWindow(IReadOnlyList<ProgressEvent> Events, string RangeLine)
{
    internal List<string> CreateSectionLines() => ["## Recent Timeline", RangeLine];

    internal static ReviewerTimelineWindow? ForTask(
        Goal goal,
        TaskSpec task,
        IEnumerable<ProgressEvent> eligibleEvents,
        int subsequentReviewBudget,
        TaskComplexity complexity)
    {
        if (task.RequiredRole != AgentRole.Reviewer)
        {
            return null;
        }

        var currentDispatchAt = task.Status == WorkTaskStatus.Running ? task.LastDispatch?.DispatchedAt : null;
        var reviewerIds = goal.Tasks
            .Where(task => task.RequiredRole == AgentRole.Reviewer)
            .Select(task => task.Id)
            .ToHashSet();
        var previousRoundAt = goal.Timeline
            .Where(evt => evt.GoalId == goal.Id &&
                evt.Kind == ProgressKind.TaskVerificationRecorded &&
                evt.TaskId is { } taskId && reviewerIds.Contains(taskId) &&
                (currentDispatchAt is null || evt.OccurredAt < currentDispatchAt.Value))
            .Select(evt => (DateTimeOffset?)evt.OccurredAt)
            .Max();

        if (previousRoundAt is { } anchor)
        {
            return new ReviewerTimelineWindow(
                PromptContextFormatter.SelectPromptTimelineEvents(
                    eligibleEvents.Where(evt => evt.OccurredAt > anchor),
                    subsequentReviewBudget,
                    complexity),
                "Range: since the previous Reviewer round. Full timeline: goal/timeline.json");
        }

        return new ReviewerTimelineWindow(
            eligibleEvents.OrderBy(evt => evt.OccurredAt).TakeLast(40).ToList(),
            "Range: all events (newest 40 maximum). Full timeline: goal/timeline.json");
    }
}
