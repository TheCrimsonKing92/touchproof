namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed record PullRequestPublishRequest(string RepositoryRoot, string GoalBranch,
    string CandidateCommit, string? PreviouslyPushedCommit, string Trunk, string Title, string Body,
    string? ForgeIdentityPath = null);
