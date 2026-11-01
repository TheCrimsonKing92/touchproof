using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class GoalLiveDispatchPredicate
{
    internal static bool HasLiveDispatch(Goal goal) =>
        goal.Tasks.Any(task => task.LastProcess is { IsRunning: true } ||
            task.CompletionBuildEvidence is { IsPending: true });
}
