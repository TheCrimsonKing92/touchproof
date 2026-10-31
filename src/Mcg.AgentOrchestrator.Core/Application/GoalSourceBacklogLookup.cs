namespace Mcg.AgentOrchestrator.Core;

internal static class GoalSourceBacklogLookup
{
    internal static Goal? Find(IEnumerable<Goal> goals, string backlogItemId)
    {
        if (string.IsNullOrWhiteSpace(backlogItemId))
            return null;

        return goals.FirstOrDefault(goal =>
            string.Equals(goal.SourceBacklogItemId, backlogItemId, StringComparison.Ordinal));
    }
}
