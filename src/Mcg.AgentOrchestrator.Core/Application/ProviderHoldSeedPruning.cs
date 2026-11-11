namespace Mcg.AgentOrchestrator.Core;

/// <summary>Runs the canonical retained-set decision on sparse provider policy inputs.</summary>
public static class ProviderHoldSeedPruning
{
    public static IReadOnlyList<string> RequiredGoals(IReadOnlyList<string> candidates,
        IReadOnlyList<ProviderUsageGoalEvidence>? evidence, ProviderHoldHistory existing,
        IReadOnlyCollection<Goal> workingSet)
    {
        // An unsupported or incomplete projection cannot justify skipping a full read.
        if (evidence is null || evidence.Count != candidates.Count || !candidates.ToHashSet(StringComparer.Ordinal)
                .SetEquals(evidence.Select(row => row.GoalId))) return candidates;
        var history = new ProviderHoldHistory();
        history.CopyFrom(existing);
        history.Retain(evidence.Select(Adapt).ToArray(), workingSet);
        // For a nonempty answer keep the original path, including every archive dependency.
        // Only an empty fixed point proves that no archived goal needs materialization.
        return history.Retained.Count == 0 ? [] : candidates;
    }

    private static Goal Adapt(ProviderUsageGoalEvidence row)
    {
        var tasks = row.Tasks.Select(task => TaskSpec.FromSnapshot(new TaskSnapshot(
            task.TaskId, "Provider seed input", task.RequiredRole, task.Status, task.AssignedAgentId,
            null, task.LastVerification, task.VerificationHistory, task.LastDispatch, null,
            DispatchHistory: task.DispatchHistory, LatestProviderBudgetRecoveryAt: task.LatestProviderBudgetRecoveryAt)))
            .ToArray();
        var goal = tasks.Length == 0
            ? Goal.CreateMetadataOnlyTerminal(new TerminalGoalMetadata(new GoalId(row.GoalId),
                GoalStatus.Completed, "Provider seed input"))
            : new Goal(new GoalId(row.GoalId), "Provider seed input", tasks);
        goal.SetStatus(row.Status);
        foreach (var evt in row.Timeline.OrderBy(evt => evt.OccurredAt)) goal.Append(evt);
        return goal;
    }
}
