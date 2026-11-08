using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;

namespace Mcg.AgentOrchestrator.App.Orchestration;

/// <summary>Owns the transition from tuple verification to ordered landing under the caller's lease.</summary>
internal static class MultiRepositoryAcceptanceExecution
{
    internal static MultiRepositoryAcceptanceResult Run(Goal goal, GoalLifecycleState state,
        MultiRepositoryAcceptanceGate? gate, MultiRepositoryTupleLanding? landing,
        Func<AcceptanceVerificationSummary> runHome, Func<LandingResult> landHome,
        Func<string?> mutationBlocker, Action clearAcceptanceFailure, Action<LandingResult> onLanded)
    {
        if (landing?.RequiresRecovery(goal) == true) return new(landing.Recover(goal, state).Hold);
        if (gate is null || !AcceptancePrecheck.HasCompletedPassedVerificationForAllTasks(goal))
            return new(new ConductorAdvanceOutcome.Held(state, "multi-repository-acceptance-pending: " +
                (gate is null ? "repository gate is not configured." : "every task requires passing verification."))
            {
                Decision = new PolicyDecisionRecord("multi-repository-acceptance", "Hold", 1,
                    "multi-repository-acceptance-pending", "next-tick", [])
            });
        var result = gate.Run(goal, state, runHome);
        if (result.Landable is not { } receipt) return result;
        clearAcceptanceFailure();
        if (mutationBlocker() is { Length: > 0 } blocked)
            return new(new ConductorAdvanceOutcome.Held(state, "multi-repository-landing-blocked: " + blocked)
            {
                Decision = new PolicyDecisionRecord("multi-repository-landing", "Hold", 1,
                    "multi-repository-landing-blocked", "next-tick", [new PolicyDecisionFact("blocker", blocked)])
            });
        if (landing is null) throw new InvalidOperationException("Tuple landing is not configured.");
        var landed = landing.Execute(goal, state, receipt, landHome, mutationBlocker);
        if (landed.HomeLanding is { } home) onLanded(home);
        return new(landed.Hold, Landing: landed.HomeLanding);
    }
}
