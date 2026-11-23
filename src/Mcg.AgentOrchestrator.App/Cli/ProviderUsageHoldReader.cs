using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class ProviderUsageHoldReader
{
    internal static IReadOnlyList<ProviderUsageHoldRow> ReadStatus(SqliteOrchestratorStateRepository repository,
        IReadOnlyCollection<Goal> loaded, IReadOnlyCollection<GoalId> failed, DateTimeOffset now) =>
        Read(repository, loaded, now, failed);

    internal static IReadOnlyList<ProviderUsageHoldRow> Read(IOrchestratorStateRepository repository,
        IReadOnlyCollection<Goal> loaded, DateTimeOffset now, IReadOnlyCollection<GoalId>? failed = null)
    {
        var loadedIds = loaded.Select(goal => goal.Id).ToHashSet();
        var failedIds = (failed ?? []).Where(id => !loadedIds.Contains(id)).ToHashSet();
        var workingIds = loadedIds.Concat(failedIds).Select(id => id.Value).ToHashSet(StringComparer.Ordinal);
        IReadOnlyCollection<Goal> working = loaded;
        try
        {
            var history = repository.ReadProviderHoldSeedHistoryAsync().GetAwaiter().GetResult();
            var selection = ProviderHoldHistory.SelectSeedEvidence(history.Rows, history.Attempts, workingIds, history.Recoveries);
            var ids = selection.Sources.Concat(selection.Dependencies).Select(id => new GoalId(id))
                .Concat(history.MarkerGoals).Concat(failedIds).Where(id => !loadedIds.Contains(id)).ToHashSet();
            var projected = history.Evidence.Where(row => ids.Contains(new GoalId(row.GoalId)))
                .ToDictionary(row => new GoalId(row.GoalId));
            var remaining = ids.Where(id => !projected.ContainsKey(id)).ToArray();
            if (remaining.Length != 0)
                foreach (var row in ProviderHoldSeedEvidenceRead.TryRead(repository, remaining) ?? [])
                    projected.Add(new GoalId(row.GoalId), row);
            var evidence = projected.Values.Where(row => row.Tasks.Count != 0)
                .Select(ProviderHoldSeedPruning.Adapt).ToDictionary(goal => goal.Id);
            var missing = ids.Where(id => !evidence.ContainsKey(id)).ToArray();
            if (missing.Length != 0)
                foreach (var goal in repository.LoadGoalsAsync(missing).GetAwaiter().GetResult().Goals)
                    evidence.TryAdd(goal.Id, goal);

            working = loaded.Concat(evidence.Values.Where(goal => failedIds.Contains(goal.Id)))
                .OrderBy(goal => goal.Id.Value, StringComparer.Ordinal).ToArray();
            var retained = evidence.Values.Where(goal => !workingIds.Contains(goal.Id.Value)).ToArray();
            return ReadRetained(working, retained, selection.Sources, now);
        }
        catch (Exception)
        {
            if (ReferenceEquals(working, loaded) && failedIds.Count != 0)
            {
                try { working = loaded.Concat(repository.LoadGoalsAsync(failedIds).GetAwaiter().GetResult().Goals).ToArray(); }
                catch (Exception) { }
            }
            try
            {
                // Indexed history can still identify readable goals when sparse discovery fails.
                var sources = ProviderHoldHistory.SeedCandidates(
                    repository.ListModelFitHistoryAsync().GetAwaiter().GetResult(), workingIds);
                if (sources.Count != 0)
                {
                    var retained = repository.LoadGoalsAsync(sources.Select(id => new GoalId(id)).ToArray())
                        .GetAwaiter().GetResult().Goals;
                    return ReadRetained(working, retained, sources.ToHashSet(StringComparer.Ordinal), now);
                }
            }
            catch (Exception) { }
            return ProviderUsageHoldView.Read(working, now);
        }
    }

    private static IReadOnlyList<ProviderUsageHoldRow> ReadRetained(IReadOnlyCollection<Goal> working,
        IReadOnlyCollection<Goal> retained, IReadOnlySet<string> sources, DateTimeOffset now)
    {
        if (retained.Count == 0) return ProviderUsageHoldView.Read(working, now);
        var index = ProviderHoldSeedIndex.Evaluate(working, [], retained, sources);
        return ProviderUsageHoldView.Read(working.Concat(retained.Where(goal =>
            index.RequiredGoals.Contains(goal.Id.Value))).ToArray(), now);
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
