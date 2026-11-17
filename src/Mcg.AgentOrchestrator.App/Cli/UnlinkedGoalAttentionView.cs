using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class UnlinkedGoalAttentionView
{
    internal static Dictionary<string, object?> WithRows(Dictionary<string, object?> payload, object[]? rows)
    {
        if (rows is { Length: > 0 }) payload.Add("unlinkedGoals", rows);
        return payload;
    }

    internal static object[] Rows(IReadOnlyList<AttentionGoalReadFacts>? goals, string portfolioStorePath)
    {
        var candidates = (goals ?? []).Where(goal => goal.Status is not
                (GoalStatus.Parked or GoalStatus.Completed or GoalStatus.Failed or GoalStatus.Cancelled or GoalStatus.Superseded))
            .DistinctBy(goal => goal.GoalId, StringComparer.Ordinal).ToArray();
        if (candidates.Length == 0) return [];
        var epics = EpicMembershipIndex.Load(portfolioStorePath);
        var rows = new List<object>();
        foreach (var goal in candidates)
        {
            var missingEpic = epics.FindGoal(goal.GoalId) is null;
            var missingBacklogLink = string.IsNullOrWhiteSpace(goal.SourceBacklogItemId);
            if (!missingEpic && !missingBacklogLink) continue;
            rows.Add(new
            {
                goalId = goal.GoalId, title = goal.Title,
                reason = missingEpic
                    ? missingBacklogLink ? "no-epic-no-backlog-link" : "no-epic"
                    : "no-backlog-link"
            });
        }
        return rows.ToArray();
    }
}
