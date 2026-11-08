using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class MultiRepositoryGoal
{
    internal const string Route = "multi-repository-tuple-landing";
    internal const string NextAction = "conductor next tick";
    internal static bool Applies(Goal goal) => goal.Repositories is { Keys.Count: > 1 };
    internal static string Reason(Goal goal) => goal.LandingBlocker?.Kind.Contains(MultiRepositoryLandingFailedHold.Kind,
        StringComparison.Ordinal) == true ? MultiRepositoryLandingFailedHold.Kind : Route;
    internal static string SuggestedAction(Goal goal) => Reason(goal) == MultiRepositoryLandingFailedHold.Kind
        ? MultiRepositoryLandingFailedHold.NextAction : NextAction;
    internal static GateReadyCandidateProjectionResult.Excluded? Exclusion(Goal goal) => Applies(goal)
        ? new(GateReadyCandidateExclusionReason.LandingHeld) { HoldReason = Reason(goal) } : null;

    internal static ConductorAdvanceOutcome.Held Routed(Goal goal, GoalLifecycleState state) => new(state,
        Route + ": " + NextAction)
    {
        Decision = new PolicyDecisionRecord("multi-repository-landing", "Hold", 1, Route, NextAction,
            [new PolicyDecisionFact("owner", "conductor")])
    };
}
