using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Owns the snapshot identity checked before failed-goal recovery effects are applied.
internal static class FailedGoalRecoveryContextVersion
{
    internal static string Build(Goal goal, GoalLifecycleState state)
    {
        var taskState = string.Join(
            ";",
            goal.Tasks.Select(task => string.Join(
                ":",
                task.Id.Value,
                task.Status,
                task.EmptyOutputRetryCount,
                task.CriterionRetryCount,
                FailedGoalFindingObservation.BuildAttemptIdentity(task),
                task.LastVerification?.CompletedAt.UtcTicks.ToString() ?? "none")));
        var lastEvent = goal.Timeline.LastOrDefault();
        return string.Join(
            "|",
            goal.Id.Value,
            goal.Status,
            state,
            goal.AuthoritativeBrief.Version,
            goal.AutomaticAcceptanceRetryCount,
            goal.Timeline.Count,
            lastEvent?.Kind.ToString() ?? "none",
            lastEvent?.OccurredAt.UtcTicks.ToString() ?? "none",
            taskState);
    }
}
