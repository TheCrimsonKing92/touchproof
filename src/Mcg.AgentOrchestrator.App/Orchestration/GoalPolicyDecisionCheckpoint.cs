using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class GoalPolicyDecisionCheckpoint
{
    internal static IReadOnlyList<GoalId> FindChangedTerminalGoals(
        AgentOrchestratorKernel kernel, IReadOnlyDictionary<GoalId, string> baseline) =>
        kernel.Goals.Where(goal => ConductorWorkingSet.IsTerminalGoal(goal) &&
            baseline.TryGetValue(goal.Id, out var before) && before != GoalKernelChange.Capture(goal))
            .Select(goal => goal.Id).ToArray();

    internal static bool Persist(
        AgentOrchestratorKernel kernel,
        IReadOnlyCollection<GoalId> goalIds,
        IReadOnlyList<ProgressEvent> decisions,
        Func<AgentOrchestratorKernel, IReadOnlyCollection<GoalId>, IReadOnlyList<GoalSnapshotCheckpointResult>>? checkpointGoalTick,
        Func<IReadOnlyList<GoalSnapshotCheckpointResult>, IReadOnlyCollection<GoalId>, IReadOnlyCollection<GoalId>> applyOutcomes,
        Action<string> emitProgress)
    {
        if (checkpointGoalTick is null || goalIds.Count == 0) return true;
        var outcomes = checkpointGoalTick(kernel, goalIds);
        var durable = applyOutcomes(outcomes, goalIds);
        var missing = MissingDecisions(outcomes, decisions);
        if (missing.Length == 0) return durable.Count == goalIds.Count;

        // Durable includes a Skipped merge. Rebase may have removed these events from
        // memory, so retry the original events once against the new authoritative baseline.
        var retryIds = missing.Select(decision => decision.GoalId).Distinct().ToArray();
        foreach (var decision in missing)
        {
            if (!kernel.Goals.Any(goal => goal.Id == decision.GoalId))
            {
                var saved = outcomes.Single(outcome => outcome.GoalId == decision.GoalId.Value).SaveResult!;
                kernel.ReplaceGoalStateWithSnapshot(saved.PersistedSnapshot!, saved.PersistedHumanInputRequests ?? []);
            }
            LoggedGoalPolicyDecisionRestorer.Restore(kernel, decision);
        }
        var retryOutcomes = checkpointGoalTick(kernel, retryIds);
        var retryDurable = applyOutcomes(retryOutcomes, retryIds);
        var stillMissing = MissingDecisions(retryOutcomes, missing);
        foreach (var decision in stillMissing)
            emitProgress($"POLICY_DECISION_CHECKPOINT_MISSING goal={decision.GoalId.Value} disposition=not-stored");
        return durable.Count == goalIds.Count && retryDurable.Count == retryIds.Length && stillMissing.Length == 0;
    }

    private static ProgressEvent[] MissingDecisions(
        IReadOnlyList<GoalSnapshotCheckpointResult> outcomes, IReadOnlyList<ProgressEvent> decisions) =>
        decisions.Where(decision => outcomes.Any(outcome => outcome.GoalId == decision.GoalId.Value &&
            outcome.IsDurable && outcome.SaveResult?.PersistedSnapshot is { } stored &&
            !stored.Timeline.Any(entry => entry.Kind == decision.Kind && entry.TaskId is null &&
                entry.OccurredAt == decision.OccurredAt && entry.Message == decision.Message))).ToArray();
}
