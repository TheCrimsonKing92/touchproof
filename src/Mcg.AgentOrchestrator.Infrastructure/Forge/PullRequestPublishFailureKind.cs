namespace Mcg.AgentOrchestrator.Infrastructure;

public enum PullRequestPublishFailureKind { NoGitHubOrigin, PushRejected, GhNotSignedIn, GhFailed, ForgeIdentityUnavailable }
