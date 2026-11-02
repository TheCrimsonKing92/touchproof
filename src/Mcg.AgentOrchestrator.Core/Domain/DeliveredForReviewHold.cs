namespace Mcg.AgentOrchestrator.Core;

public static class DeliveredForReviewHold
{
    public const string Prefix = "delivered for review: ";

    public static bool IsCurrent(Goal goal) =>
        goal.Status == GoalStatus.Verified && goal.DeliveredForReview is { } delivery &&
        goal.CurrentHold?.Blocker == Text(delivery);

    public static string Text(GoalDeliveredForReview delivery) => Text(delivery.Url, delivery.ActingLogin);

    public static string Text(string url, string? actingLogin)
    {
        var reason = Prefix + url;
        if (actingLogin is not null) reason += $" as {actingLogin}";
        return reason;
    }

    public static bool IsDeliveredForReviewText(string reason) => reason.StartsWith(Prefix, StringComparison.Ordinal);
}
