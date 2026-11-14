using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class ProviderUsageHoldReader
{
    internal static IReadOnlyList<ProviderUsageHoldRow> ReadStatus(SqliteOrchestratorStateRepository repository,
        IReadOnlyCollection<Goal> loaded, IReadOnlyCollection<GoalId> failed, DateTimeOffset now)
    {
        IReadOnlyCollection<Goal> working = loaded;
        try
        {
            // Failed goals affect holds, but are not printed goal rows. Historical candidates may
            // be Completed; neither dependency requires restoring its full task/brief graph.
            var workingIds = loaded.Select(goal => goal.Id.Value).Concat(failed.Select(id => id.Value))
                .ToHashSet(StringComparer.Ordinal);
            var candidates = ProviderHoldHistory.SeedCandidates(
                repository.ListProviderHoldHistoryAsync(TaskOutcomeRules.ProviderBudgetExhausted.Token).GetAwaiter().GetResult(), workingIds);
            var ids = failed.Concat(candidates.Select(id => new GoalId(id))).Distinct().ToArray();
            var evidence = repository.ReadProviderHoldSeedEvidenceAsync(ids).GetAwaiter().GetResult();
            if (evidence is null) throw new InvalidOperationException("Provider evidence unavailable.");
            var adapted = evidence.Select(Adapt).ToArray();
            working = loaded.Concat(adapted.Where(goal => workingIds.Contains(goal.Id.Value)))
                .OrderBy(goal => goal.Id.Value, StringComparer.Ordinal).ToArray();
            if (candidates.Count == 0) return ProviderUsageHoldView.Read(working, now);
            var history = new ProviderHoldHistory();
            history.Retain(adapted.Where(goal => !workingIds.Contains(goal.Id.Value)).ToArray(), working);
            return ProviderUsageHoldView.Read(history.Scope(working), now);
        }
        catch (Exception)
        {
            // Preserve the legacy failure path if sparse evidence is unsupported or unreadable.
            try
            {
                working = loaded.Concat(repository.LoadGoalsAsync(failed).GetAwaiter().GetResult().Goals)
                    .OrderBy(goal => goal.Id.Value, StringComparer.Ordinal).ToArray();
            }
            catch (Exception) { }
            return Read(repository, working, now);
        }
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

    internal static IReadOnlyList<ProviderUsageHoldRow> Read(IOrchestratorStateRepository repository,
        IReadOnlyCollection<Goal> loaded, DateTimeOffset now)
    {
        try
        {
            var candidates = ProviderHoldHistory.SeedCandidates(
                repository.ListModelFitHistoryAsync().GetAwaiter().GetResult(),
                loaded.Select(goal => goal.Id.Value).ToHashSet(StringComparer.Ordinal));
            if (candidates.Count == 0) return ProviderUsageHoldView.Read(loaded, now);
            var retained = repository.LoadGoalsAsync(candidates.Select(id => new GoalId(id)).ToArray()).GetAwaiter().GetResult();
            var history = new ProviderHoldHistory();
            history.Retain(retained.Goals, loaded);
            return ProviderUsageHoldView.Read(history.Scope(loaded), now);
        }
        catch (Exception) { return ProviderUsageHoldView.Read(loaded, now); }
    }

    internal static IReadOnlyList<ProviderUsageHoldRow> ReadWorkspace(OrchestratorWorkspace workspace, DateTimeOffset now)
    {
        try
        {
            if (!File.Exists(workspace.SqliteStatePath)) return [];
            var repository = SqliteOrchestratorStateRepository.OpenReadOnly(workspace.SqliteStatePath);
            var metadata = repository.ListGoalMetadataAsync().GetAwaiter().GetResult();
            var ids = metadata.Where(item => Enum.TryParse<GoalStatus>(item.Status, true, out var status) &&
                    status is not (GoalStatus.Completed or GoalStatus.Cancelled or GoalStatus.Superseded))
                .Select(item => new GoalId(item.Id)).ToArray();
            return Read(repository, repository.LoadGoalsAsync(ids).GetAwaiter().GetResult().Goals, now);
        }
        catch (Exception) { return []; }
    }
}
