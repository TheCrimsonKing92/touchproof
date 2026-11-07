using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class MultiRepositoryLandingHold
{
    internal const string Kind = "multi-repository-landing-not-built";
    internal const string NextAction = "wait for the landing slice";
    internal static bool Applies(Goal goal) => goal.Repositories is { Keys.Count: > 1 };

    internal static GateReadyCandidateProjectionResult.Excluded? Exclusion(Goal goal) => Applies(goal)
        ? new(GateReadyCandidateExclusionReason.LandingHeld) { HoldReason = Kind } : null;

    internal static ConductorAdvanceOutcome.Held Create(Goal goal, GoalLifecycleState state, MultiRepositoryAcceptanceReceipt receipt)
    {
        if (receipt.GoalId != goal.Id || !receipt.Passed || !receipt.IsComplete(goal.Repositories!.Repositories))
            throw new InvalidOperationException("A complete passing receipt is required before the landing hold.");
        return new(state, Kind + ": " + NextAction + ". Owner: operator.", Kind + ":" + receipt.TupleCanonical)
        {
            Decision = new PolicyDecisionRecord("multi-repository-landing", "Hold", 1, Kind, NextAction,
                [new PolicyDecisionFact("owner", "operator"), new PolicyDecisionFact("next_action", NextAction),
                 new PolicyDecisionFact("repository_tuple", receipt.TupleCanonical)])
        };
    }
}
