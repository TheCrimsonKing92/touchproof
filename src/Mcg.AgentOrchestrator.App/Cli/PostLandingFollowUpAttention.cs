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
        IReadOnlyList<HumanInputRequest> waits, IReadOnlyCollection<Goal>? goals)
    {
        var rows = Rows(waits, goals);
        if (rows.Length > 0)
            payload.Add("postLandingFollowUps", rows);
        return payload;
    }

    internal static object[] Rows(IReadOnlyList<HumanInputRequest> waits, IReadOnlyCollection<Goal>? goals)
    {
        var landed = (goals ?? []).Where(goal => goal.Status == GoalStatus.Completed)
            .ToDictionary(goal => goal.Id);
        return waits.Where(wait => landed.TryGetValue(wait.GoalId, out var goal) && IsFollowUp(goal, wait))
            .Select(wait => (object)new
            {
                waitId = wait.Id.Value,
                goalId = wait.GoalId.Value,
                goalState = "landed",
                availability = wait.PlannerEvidence!.Availability,
                owner = wait.EvidenceOwner ?? "operator",
                nextAction = wait.ResumeCommand
            }).ToArray();
    }
}
