namespace Mcg.AgentOrchestrator.Core;

// Publication for review is not a promotion or evidence of a merged pull request.
public sealed record GoalDeliveredForReview
{
    public GoalDeliveredForReview(int pullRequestNumber, string url, string pushedCommit,
        string gateAttemptId, DateTimeOffset deliveredAt)
    {
        if (pullRequestNumber <= 0) throw new ArgumentOutOfRangeException(nameof(pullRequestNumber));
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            uri.Host != "github.com" || !string.IsNullOrEmpty(uri.UserInfo) ||
            uri.Query.Length > 0 || uri.Fragment.Length > 0 ||
            !System.Text.RegularExpressions.Regex.IsMatch(uri.AbsolutePath, $@"\A/[^/]+/[^/]+/pull/{pullRequestNumber}\z"))
            throw new ArgumentException("Delivery requires a GitHub pull request URL.", nameof(url));
        ArgumentException.ThrowIfNullOrWhiteSpace(pushedCommit);
        if (pushedCommit.Length is not (40 or 64) || !pushedCommit.All(Uri.IsHexDigit))
            throw new ArgumentException("Delivery requires a full Git revision.", nameof(pushedCommit));
        ArgumentException.ThrowIfNullOrWhiteSpace(gateAttemptId);
        if (deliveredAt == default || deliveredAt.Offset != TimeSpan.Zero)
            throw new ArgumentException("Delivery time must be a non-default UTC instant.", nameof(deliveredAt));
        PullRequestNumber = pullRequestNumber;
        Url = url;
        PushedCommit = pushedCommit.ToLowerInvariant();
        GateAttemptId = gateAttemptId;
        DeliveredAt = deliveredAt;
    }

    public int PullRequestNumber { get; }
    public string Url { get; }
    public string PushedCommit { get; }
    public string GateAttemptId { get; }
    public DateTimeOffset DeliveredAt { get; }
}
