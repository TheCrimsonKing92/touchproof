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
        var buildRootGoals = ObserveConductorBuildRootGoals(
            kernel, goalReloads, evictedGoals, checkpointHeldGoalIds, s_ownedRootState.BuildRootGate);

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

    internal static (GoalId Id, bool IsTerminal)[] ObserveConductorBuildRootGoals(
        AgentOrchestratorKernel kernel,
        IReadOnlyDictionary<string, ConductorGoalReloadObservation> goalReloads,
        IReadOnlyDictionary<string, GoalStatus> evictedGoals,
        IReadOnlySet<string> checkpointHeldGoalIds,
        BuildRootReapCadenceGate gate)
    {
        // Checkpoint eviction leaves terminal metadata even when the next reload omits the goal.
        // Probe only ids the gate saw live. Current reload and aggregate observations take precedence.
        var checkpointedTerminals = gate.SnapshotPreviouslyLiveGoals()
            .Where(id => kernel.TryGetKnownDependencyGoalStatus(id, out var status) &&
                Enum.TryParse<GoalStatus>(status, out var parsedStatus) && IsTerminalSweepStatus(parsedStatus))
            .Select(id => (Id: id, IsTerminal: true));

        // The sweep and its remediation retry share this pre-reconciliation observation.
        return goalReloads.Where(pair =>
                pair.Value is ConductorGoalReloadObservation.Terminal terminal &&
                IsTerminalSweepStatus(terminal.Status))
            .Select(pair => (Id: new GoalId(pair.Key), IsTerminal: true))
            .Concat(ObserveBuildRootGoals(kernel, evictedGoals))
            .Concat(checkpointedTerminals)
            .Where(goal => !checkpointHeldGoalIds.Contains(goal.Id.Value))
            .ToArray();
    }

    private static IEnumerable<(GoalId Id, bool IsTerminal)> ObserveBuildRootGoals(
        AgentOrchestratorKernel kernel, IReadOnlyDictionary<string, GoalStatus>? evicted = null) =>
        (evicted?.Select(pair => (new GoalId(pair.Key), IsTerminalSweepStatus(pair.Value))) ?? [])
        .Concat(kernel.Goals.Select(goal => (goal.Id, IsTerminalSweepStatus(goal.Status))));
}
