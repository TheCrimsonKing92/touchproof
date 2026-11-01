using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Coordinates persisted gate waits without changing the conductor's hold decisions.
internal static class ConductorGateAttention
{
    internal static void Configure(
        ConductorDriver driver, string orchestratorDirectory, Func<Goal, PassedGateHold?> resolvePassedGateHold)
    {
        var store = CollaborationItemStore.ForDirectory(orchestratorDirectory);
        driver.PassedGateHoldAttention = new(store, resolvePassedGateHold);
        driver.AwaitingVerificationGateAttention = new(store);
    }

    internal static void ReconcileAll(
        ConductorDriver driver, IEnumerable<Goal> goals, Action<string, Func<bool>> runPhase)
    {
        runPhase("reconcile-passed-gate-holds", () =>
        {
            driver.PassedGateHoldAttention?.ReconcileAll(goals);
            return true;
        });
        runPhase("reconcile-awaiting-verification-waits", () =>
        {
            driver.AwaitingVerificationGateAttention?.ReconcileAll(goals);
            return true;
        });
    }

    internal static void ReconcileOutcome(
        AgentOrchestratorKernel kernel, ConductorDriver driver, Goal goal, ConductorAdvanceOutcome outcome)
    {
        PassedGateHoldFaultIsolation.Run(goal, "reconcile-outcome",
            () => driver.PassedGateHoldAttention?.Reconcile(kernel.GetGoal(goal.Id)));
        PassedGateHoldFaultIsolation.Run(goal, "reconcile-awaiting-verification-outcome",
            () => driver.AwaitingVerificationGateAttention?.Reconcile(kernel.GetGoal(goal.Id),
                outcome is ConductorAdvanceOutcome.Held { State: GoalLifecycleState.AwaitingVerification }));
    }
}
