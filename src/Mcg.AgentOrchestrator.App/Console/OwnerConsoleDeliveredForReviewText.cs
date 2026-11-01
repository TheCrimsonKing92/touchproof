using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal static class OwnerConsoleDeliveredForReviewText
{
    internal static string? For(Goal goal)
    {
        if (goal.Status != GoalStatus.Verified || goal.DeliveredForReview is not { } delivery ||
            goal.CurrentHold?.Blocker != "delivered for review: " + delivery.Url ||
            goal.Timeline.Any(item => item.Kind == ProgressKind.GoalPolicyDecision &&
                item.OccurredAt >= delivery.DeliveredAt && item.Message == PullRequestLandingDelivery.ClosedUnmergedPrefix + delivery.Url))
            return null;
        return $"delivered for review: PR {delivery.PullRequestNumber}";
    }
}
