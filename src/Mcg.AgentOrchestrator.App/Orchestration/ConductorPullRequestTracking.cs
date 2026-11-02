using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Pre-walk tracking readmits active refreshes and retains all changed goals for persistence.
internal static class ConductorPullRequestTracking
{
    internal static IReadOnlySet<GoalId> Service(
        PullRequestDeliveryTracker? tracker, IReadOnlyCollection<Goal> goals,
        IReadOnlySet<GoalId> hostedChangedGoalIds, ISet<string> excludedGoals,
        IDictionary<string, BatchSetAsideEntry> setAsideGoals, ISet<string> escalatedGoals,
        GoalProjectionCache goalProjectionCache)
    {
        var trackedChanges = tracker?.Service(goals) ?? new HashSet<GoalId>();
        var changedGoalIds = hostedChangedGoalIds.Concat(trackedChanges).ToHashSet();
        foreach (var goal in goals.Where(goal => trackedChanges.Contains(goal.Id) &&
            goal.Status == GoalStatus.Verified && goal.CurrentHold is null &&
            PullRequestLandingDelivery.HasRefreshStarted(goal) && !PullRequestLandingDelivery.RefreshStopped(goal)))
        {
            excludedGoals.Remove(goal.Id.Value);
            setAsideGoals.Remove(goal.Id.Value);
            escalatedGoals.Remove(goal.Id.Value);
            goalProjectionCache.Invalidate(goal.Id);
        }
        return changedGoalIds;
    }
}
