namespace Mcg.AgentOrchestrator.Core;

public static class RetryCorrectionObligationBriefSection
{
    public const string CurrentHeading = "## Current correction obligation";
    public const string HistoryHeading = "## Retry and review history";

    public static string Projection(RetryCorrectionObligation obligation, string message) =>
        string.Join(Environment.NewLine,
            CurrentHeading,
            $"kind={obligation.Kind}; candidate_sha={obligation.CandidateSha}; condition={obligation.ConditionIdentity}; feedback_sha256={obligation.FeedbackSha256}",
            obligation.Kind == RetryCorrectionObligationKind.Structural
                ? "A no-change result requires this condition to pass at this candidate. If the condition cannot be met, report blockers: present."
                : "Satisfy this correction through the normal review and evidence path.",
            RetryCorrectionObligation.NormalizeFeedback(message).Replace("\n", Environment.NewLine, StringComparison.Ordinal));

    public static IEnumerable<string> RenderCurrent(TaskSpec task) =>
        task.AcceptedRetryFeedback is { Obligation: { } obligation } accepted
            ? [Projection(obligation, accepted.Message), string.Empty, HistoryHeading, string.Empty]
            : [];

    public static IEnumerable<TaskBriefSegment> RenderRetryFeedbackProjection(TaskSpec task)
    {
        if (task.CriterionRetryFeedback.Count == 0) return [];
        var lines = task.AcceptedRetryFeedback is { Obligation: { } obligation } accepted
            ? new List<string> { Projection(obligation, accepted.Message), string.Empty, HistoryHeading }
            : new List<string> { "## Unmet acceptance criteria from the prior attempt - fix these:" };
        lines.AddRange(task.CriterionRetryFeedback
            .Where(item => task.AcceptedRetryFeedback?.Obligation is null || item != task.AcceptedRetryFeedback.Message)
            .Select(item => $"- {item}"));
        lines.Add(string.Empty);
        return [TaskBriefSegment.Projected("task/criterion-retry-feedback.json", lines)];
    }
}
