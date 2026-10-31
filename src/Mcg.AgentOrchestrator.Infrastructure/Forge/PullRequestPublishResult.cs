namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed record PullRequestPublishResult(int? PullRequestNumber, string? Url, string? PushedCommit,
    PullRequestPublishFailureKind? FailureKind = null, string? Message = null, string? PushedBranch = null)
{
    public bool Succeeded => FailureKind is null && PullRequestNumber > 0 && Url is not null && PushedCommit is not null;
    public string? FailureToken => FailureKind switch
    {
        PullRequestPublishFailureKind.NoGitHubOrigin => "no-github-origin",
        PullRequestPublishFailureKind.PushRejected => "push-rejected",
        PullRequestPublishFailureKind.GhNotSignedIn => "gh-not-signed-in",
        PullRequestPublishFailureKind.GhFailed => "gh-failed",
        _ => null
    };
}
