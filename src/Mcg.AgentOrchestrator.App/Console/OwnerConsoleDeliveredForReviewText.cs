using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal static class OwnerConsoleDeliveredForReviewText
{
    internal static string? For(Goal goal)
    {
        if (!DeliveredForReviewHold.IsCurrent(goal) || goal.DeliveredForReview is not { } delivery ||
            goal.Timeline.Any(item => item.Kind == ProgressKind.GoalPolicyDecision &&
                item.OccurredAt >= delivery.DeliveredAt && item.Message == PullRequestLandingDelivery.ClosedUnmergedPrefix + delivery.Url))
            return null;
        return $"delivered for review: PR {delivery.PullRequestNumber}";
    }
}
