using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorBatchLoop
{
    private IEnumerable<(ConductorLandingReceipt Receipt, bool ChangedFilesAvailable)> RecoverSweepLandingReceipts(
        TerminalGoalSweepResult? sweepResult, IReadOnlyCollection<GoalId> terminalizedGoalIds)
    {
        foreach (var recovered in sweepResult?.Goals ?? [])
        {
            if (recovered.Reconciliation is not { } reconciliation ||
                !terminalizedGoalIds.Contains(recovered.GoalId) ||
                _landingRelaunchDecisions.Contains(recovered.GoalId.Value)) continue;
            var changedFiles = _workspace is null ? null : LandingExecutor.TryResolveRecoveredChangedFiles(
                _workspace.ExecutionDirectory, null, reconciliation.IntegrateSha);
            yield return (new ConductorLandingReceipt(recovered.GoalId.Value, changedFiles ?? [], reconciliation.IntegrateSha),
                changedFiles is not null);
        }
    }

    private static IReadOnlyCollection<GoalId> PersistSweepTerminalizations(
        TerminalGoalSweepResult? sweepResult,
        AgentOrchestratorKernel kernel,
        Func<AgentOrchestratorKernel, IReadOnlyCollection<GoalId>, IReadOnlyList<GoalSnapshotCheckpointResult>>? checkpointGoalTick,
        Action<AgentOrchestratorKernel, IReadOnlyCollection<GoalId>>? persistGoalTick,
        Dictionary<string, GoalSnapshotCheckpointResult> checkpointHeldGoals,
        int tick,
        List<string> tickLines,
        Action<TimeSpan>? busyWriteDelay, IReadOnlyDictionary<GoalId, string> preSweepBaseline)
    {
        // The host can fault after the sweep has mutated goals but before returning its list.
        var goalIds = sweepResult?.ExplicitlyTerminalizedGoalIds ?? (checkpointGoalTick is null
            ? [] : GoalPolicyDecisionCheckpoint.FindChangedTerminalGoals(kernel, preSweepBaseline));
        if (goalIds.Count > 0 && checkpointGoalTick is not null)
        {
            var decisions = sweepResult?.TerminalizationDecisions ?? kernel.Goals
                .Where(goal => goalIds.Contains(goal.Id)).SelectMany(goal => goal.Timeline)
                .Where(entry => entry.Kind == ProgressKind.GoalPolicyDecision &&
                    entry.Message.StartsWith("Goal terminalized from merge evidence at", StringComparison.Ordinal)).ToArray();
            GoalPolicyDecisionCheckpoint.Persist(kernel, goalIds, decisions, checkpointGoalTick,
                (outcomes, ids) => ApplyCheckpointOutcomes(outcomes, ids, checkpointHeldGoals, tick, "sweep-terminalization", tickLines), line => EmitProgress(line, tickLines));
        }
        else if (goalIds.Count > 0 && persistGoalTick is not null)
        {
            PersistGoalTickOrThrow(persistGoalTick, kernel, goalIds, tick, tickLines, busyWriteDelay);
        }
        return goalIds;
    }
}
