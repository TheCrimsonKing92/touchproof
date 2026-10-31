namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed record PullRequestStatus(PullRequestStatusKind Kind, string? MergeCommit = null,
    string? Url = null, string? Reason = null, string? Repository = null);
