namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed record PullRequestChangesRequest(string Login, DateTimeOffset SubmittedAt, string Body);
