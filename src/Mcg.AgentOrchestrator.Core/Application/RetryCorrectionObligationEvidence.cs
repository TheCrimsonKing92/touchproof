namespace Mcg.AgentOrchestrator.Core;

// These markers are appended by the dispatch runner after worker output. Always
// inspect the last marker, so an earlier claim cannot override the runner's verdict.
public static class RetryCorrectionObligationEvidence
{
    public const string DeclineCode = "structural-obligation-unsatisfied";
    public const string Marker = "@@MCG_RETRY_OBLIGATION@@";

    public static string Format(RetryCorrectionObligation obligation, RetryCorrectionConditionVerdict verdict) =>
        $"{Marker} outcome={verdict.ToString().ToLowerInvariant()}; condition={obligation.ConditionIdentity}; " +
        $"candidate_sha={obligation.CandidateSha.ToLowerInvariant()}; feedback_sha256={obligation.FeedbackSha256}";

    public static bool IsSatisfied(TaskSpec task, string? standardError)
    {
        var obligation = task.AcceptedRetryFeedback?.Obligation;
        if (task.RequiredRole != AgentRole.Developer ||
            obligation?.AppliesToSizeRatchet(task.LastDispatch?.BaseCommit) != true)
            return true;

        var latest = (standardError ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n').LastOrDefault(line => line.StartsWith(Marker, StringComparison.Ordinal));
        return obligation.FeedbackSha256 == RetryCorrectionObligation.HashFeedback(task.AcceptedRetryFeedback!.Message) &&
            string.Equals(latest, Format(obligation, RetryCorrectionConditionVerdict.Passed), StringComparison.Ordinal);
    }
}
