namespace Mcg.AgentOrchestrator.Core;

public static class DeliveredForReviewHold
{
    public static bool IsCurrent(Goal goal) =>
        goal.Status == GoalStatus.Verified && goal.DeliveredForReview is { } delivery &&
        goal.CurrentHold?.Blocker == Text(delivery);

    public static string Text(GoalDeliveredForReview delivery)
    {
        var reason = $"delivered for review: {delivery.Url}";
        if (delivery.ActingLogin is not null) reason += $" as {delivery.ActingLogin}";
        return reason;
    }
}
