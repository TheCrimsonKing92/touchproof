using System.Globalization;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class PostLandingFollowUpAttention
{
    internal const string Label = "post-landing follow-up; goal landed";

    internal static bool IsFollowUp(Goal goal, HumanInputRequest request) =>
        goal.Id == request.GoalId && goal.Status == GoalStatus.Completed &&
        !request.IsCompleted && PostLandingOperatorCheck.IsPostLandingCheck(request);

    internal static bool IsFollowUp(GoalSummary goal, HumanInputRequestSnapshot request) =>
        string.Equals(goal.Id, request.GoalId, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(goal.Status, nameof(GoalStatus.Completed), StringComparison.OrdinalIgnoreCase) &&
        !request.IsCompleted && request.Kind == HumanWaitKind.ProspectiveAcceptanceEvidence &&
        string.Equals(request.PlannerEvidence?.Availability, PostLandingOperatorCheck.Availability,
            StringComparison.OrdinalIgnoreCase);

    internal static Dictionary<string, object?> WithRows(Dictionary<string, object?> payload,
        IReadOnlyList<HumanInputRequest> waits, IReadOnlyCollection<Goal>? goals, DateTimeOffset renderedAt)
    {
        var landed = (goals ?? []).Where(goal => goal.Status == GoalStatus.Completed)
            .ToDictionary(goal => goal.Id);
        var followUps = waits.Where(wait => landed.TryGetValue(wait.GoalId, out var goal) && IsFollowUp(goal, wait))
            .ToArray();
        if (followUps.Length == 0)
            return payload;

        payload.Add("postLandingFollowUps", followUps.Select(wait => new
        {
            waitId = wait.Id.Value,
            goalId = wait.GoalId.Value,
            goalState = "landed",
            availability = wait.PlannerEvidence!.Availability,
            owner = wait.EvidenceOwner ?? "operator",
            nextAction = wait.ResumeCommand,
            requestedAt = wait.RequestedAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
            ageSeconds = AgeSeconds(wait, renderedAt)
        }).ToArray());
        var oldest = followUps.OrderBy(wait => wait.RequestedAt)
            .ThenBy(wait => wait.Id.Value, StringComparer.Ordinal).First();
        payload.Add("postLandingFollowUpSummary", new
        {
            open = followUps.Length,
            oldestAgeSeconds = AgeSeconds(oldest, renderedAt),
            oldestWaitId = oldest.Id.Value
        });
        return payload;
    }

    private static long AgeSeconds(HumanInputRequest wait, DateTimeOffset renderedAt) =>
        Math.Max(0L, (renderedAt - wait.RequestedAt).Ticks / TimeSpan.TicksPerSecond);

    // The envelope reads its clock too; freeze it so generatedAt and ages share one instant.
    internal static TimeProvider CaptureRenderTime(TimeProvider? time) =>
        new RenderTime((time ?? TimeProvider.System).GetUtcNow());

    private sealed class RenderTime(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
