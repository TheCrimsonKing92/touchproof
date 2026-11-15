using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Owns the snapshot identity checked before failed-goal recovery effects are applied.
internal static class FailedGoalRecoveryContextVersion
{
    internal static bool IsCurrent(
        ConductorDriver driver,
        Goal current,
        ConductorAutonomyPolicy policy,
        GoalLifecycleState expectedState,
        FailedGoalRecoveryDecision decision,
        out string reason)
    {
        var state = GoalLifecycle.ResolveState(current, driver.GetFacts(current));
        var currentFacts = driver.BuildFailedGoalRecoveryFacts(current, policy, state);
        if (state == expectedState &&
            string.Equals(currentFacts.ContextVersion, decision.Identity.ContextVersion, StringComparison.Ordinal))
        {
            reason = string.Empty;
            return true;
        }

        reason = $"{decision.Reason} Recovery effects were held because the goal/task/attempt authority changed before application " +
            $"(stale-recovery-facts; expected={decision.Identity.ContextVersion}; actual={currentFacts.ContextVersion}; state={state}).";
        return false;
    }

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
