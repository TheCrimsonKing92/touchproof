using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static partial class TerminalGoalSweep
{
    internal static (TerminalGoalSweepResult Sweep, ReconcileSweepRemediationOutcome Remediation) RunForConductor(
        AgentOrchestratorKernel kernel,
        OrchestratorWorkspace workspace,
        TerminalGoalSweepCache cache,
        GoalWorktreeCleanupHooks cleanupHooks,
        ReconcileSweepRemediationCoordinator coordinator,
        bool admitRemedies,
        int buildRootReapIntervalSeconds,
        IReadOnlyDictionary<string, ConductorGoalReloadObservation> goalReloads,
        IReadOnlyDictionary<string, GoalStatus> evictedGoals,
        IReadOnlySet<string> checkpointHeldGoalIds)
    {
        // The first sweep and its remediation retry share the same observation. Terminal status
        // created during reconciliation can trigger retention after checkpointing.
        var buildRootGoals = goalReloads.Where(pair =>
                pair.Value is ConductorGoalReloadObservation.Terminal terminal &&
                IsTerminalSweepStatus(terminal.Status))
            .Select(pair => (Id: new GoalId(pair.Key), IsTerminal: true))
            .Concat(ObserveBuildRootGoals(kernel, evictedGoals))
            .Where(goal => !checkpointHeldGoalIds.Contains(goal.Id.Value))
            .ToArray();

        TerminalGoalSweepResult Sweep() => ConductorTickStepLedger.Measure("terminal-sweep-run", () => Run(
            kernel, workspace.ExecutionDirectory, workspace.IntegrationBranch, cache: cache,
            cleanupHooks: cleanupHooks, workspace: workspace,
            buildRootReapIntervalSeconds: buildRootReapIntervalSeconds, buildRootGoalObservation: buildRootGoals));

        var terminalSweep = Sweep();
        var remediation = ConductorTickStepLedger.Measure("reconcile-remediation", () => coordinator.Process(terminalSweep, admitRemedies));
        if (remediation.RemedySucceeded)
            terminalSweep = Sweep().PreserveTerminalizationsFrom(terminalSweep);
        return (terminalSweep, remediation);
    }

    private static IEnumerable<(GoalId Id, bool IsTerminal)> ObserveBuildRootGoals(
        AgentOrchestratorKernel kernel, IReadOnlyDictionary<string, GoalStatus>? evicted = null) =>
        (evicted?.Select(pair => (new GoalId(pair.Key), IsTerminalSweepStatus(pair.Value))) ?? [])
        .Concat(kernel.Goals.Select(goal => (goal.Id, IsTerminalSweepStatus(goal.Status))));
}
