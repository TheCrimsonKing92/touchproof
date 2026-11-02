namespace Mcg.AgentOrchestrator.Core;

internal static class DownstreamInvalidationMessage
{
    internal const string Prefix = "Invalidated ";

    internal static string UpstreamRetried(
        AgentRole downstreamRole, AgentRole upstreamRole, TaskId upstreamTaskId) =>
        $"{Prefix}{downstreamRole} task because upstream {upstreamRole} task {upstreamTaskId.Value[..8]} was retried.";

    internal static string RetriedWithReason(
        AgentRole downstreamRole, AgentRole upstreamRole, TaskId upstreamTaskId, string invalidationReason) =>
        $"{Prefix}{downstreamRole} task because retried upstream {upstreamRole} task {upstreamTaskId.Value[..8]} {invalidationReason}.";

    internal static string SupersededVerdict(AgentRole downstreamRole, string reviewedCandidate, string reason) =>
        $"{Prefix}superseded {downstreamRole} verdict for reviewed candidate {reviewedCandidate} because {reason}; historical findings remain in verification history.";

    internal static bool IsInvalidation(ProgressEvent evt) =>
        evt.Kind == ProgressKind.TaskRetried &&
        evt.Message.StartsWith(Prefix, StringComparison.Ordinal) &&
        ((evt.Message.Contains(" task because upstream ", StringComparison.Ordinal) &&
          evt.Message.EndsWith(" was retried.", StringComparison.Ordinal)) ||
         (evt.Message.Contains(" task because retried upstream ", StringComparison.Ordinal) &&
          evt.Message.EndsWith(".", StringComparison.Ordinal)) ||
         (evt.Message.StartsWith($"{Prefix}superseded ", StringComparison.Ordinal) &&
          evt.Message.Contains(" verdict for reviewed candidate ", StringComparison.Ordinal) &&
          evt.Message.Contains(" because ", StringComparison.Ordinal) &&
          evt.Message.EndsWith("; historical findings remain in verification history.", StringComparison.Ordinal)));
}
