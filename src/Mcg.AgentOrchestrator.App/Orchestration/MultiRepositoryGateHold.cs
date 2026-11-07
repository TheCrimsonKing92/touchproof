using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class MultiRepositoryGateHold
{
    internal const string Kind = "multi-repository-gate-not-built";
    internal const string NextAction = "Wait for the multi-repository gate slice";
    internal static bool Applies(Goal goal) => goal.Repositories is { Keys.Count: > 1 };

    internal static GateReadyCandidateProjectionResult.Excluded? Exclusion(Goal goal) => Applies(goal)
        ? new(GateReadyCandidateExclusionReason.LandingHeld) { HoldReason = Kind } : null;

    internal static ConductorAdvanceOutcome.Held Create(Goal goal, GoalLifecycleState state) =>
        new(state, Kind + ": " + NextAction + ". Owner: operator.", Kind + ":" + string.Join(',', goal.Repositories!.Keys))
        {
            Decision = new PolicyDecisionRecord("multi-repository-gate", "Hold", 1, Kind, NextAction,
                [new PolicyDecisionFact("owner", "operator"), new PolicyDecisionFact("next_action", NextAction)])
        };
}
