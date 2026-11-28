using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Usage;

internal readonly record struct DispatchUsageIdentity(string GoalId, string TaskId, int Sequence);

internal sealed record DispatchUsagePopulation(
    IReadOnlyList<(AgentRole Role, TaskDispatchRecord Dispatch)> Dispatches,
    int Live,
    int Archived,
    IReadOnlyList<DispatchUsageIdentity> Conflicts)
{
    internal static DispatchUsagePopulation Read(
        SqliteOrchestratorStateRepository repository, DateTimeOffset? since = null, string? role = null,
        DateTimeOffset? until = null)
    {
        // Archival publishes the archive row and hot stub in one transaction. Reading live
        // first can observe both copies during the handoff, but cannot miss both.
        var eligible = Eligibility(since, role, until);
        var live = ReadLive(repository.LoadAsync().GetAwaiter().GetResult().Goals, eligible);
        var archived = repository.ReadArchivedDispatchUsageAsync(eligible).GetAwaiter().GetResult();
        return MergeArchived(live, archived);
    }

    internal static DispatchUsagePopulation Build(
        IEnumerable<Goal> goals, DateTimeOffset? since = null, string? role = null,
        IEnumerable<ArchivedDispatchUsage>? archived = null, DateTimeOffset? until = null)
    {
        var eligible = Eligibility(since, role, until);
        return MergeArchived(ReadLive(goals, eligible),
            (archived ?? []).Where(row => eligible(row.Role, row.Dispatch.DispatchedAt)));
    }

    private static Func<AgentRole, DateTimeOffset, bool> Eligibility(
        DateTimeOffset? since, string? role, DateTimeOffset? until) =>
        (requiredRole, dispatchedAt) => (since is null || dispatchedAt >= since.Value) &&
            (until is null || dispatchedAt < until.Value) &&
            (role is null || requiredRole.ToString().Equals(role, StringComparison.OrdinalIgnoreCase));

    private static Dictionary<DispatchUsageIdentity, Selection> ReadLive(
        IEnumerable<Goal> goals, Func<AgentRole, DateTimeOffset, bool> eligible)
    {
        var live = new Dictionary<DispatchUsageIdentity, Selection>();
        foreach (var goal in goals)
        foreach (var task in goal.Tasks)
        for (var sequence = 0; sequence < task.DispatchHistory.Count; sequence++)
        {
            var dispatch = task.DispatchHistory[sequence];
            if (eligible(task.RequiredRole, dispatch.DispatchedAt))
                live.Add(new(goal.Id.Value, task.Id.Value, sequence), new(task.RequiredRole, dispatch, null, HasLive: true));
        }
        return live;
    }

    private static DispatchUsagePopulation MergeArchived(
        Dictionary<DispatchUsageIdentity, Selection> selections, IEnumerable<ArchivedDispatchUsage> archived)
    {
        foreach (var row in archived)
        {
            var identity = new DispatchUsageIdentity(row.GoalId, row.TaskId, row.Sequence);
            var candidate = new Selection(row.Role, row.Dispatch, row.ArchivedAt, HasLive: false);
            if (!selections.TryGetValue(identity, out var current))
            {
                selections.Add(identity, candidate);
                continue;
            }

            var order = ComparePrecedence(candidate, current);
            if (order > 0)
                selections[identity] = candidate with { HasLive = current.HasLive };
            else if (order == 0 && !current.Conflicting && !SameUsage(candidate, current))
                selections[identity] = current with { Conflicting = true };
        }

        var dispatches = new List<(AgentRole, TaskDispatchRecord)>();
        var conflicts = new List<DispatchUsageIdentity>();
        var live = 0;
        foreach (var (identity, selection) in selections)
        {
            if (selection.Conflicting)
            {
                conflicts.Add(identity);
                continue;
            }
            dispatches.Add((selection.Role, selection.Dispatch));
            if (selection.HasLive)
                live++;
        }
        return new(dispatches, live, dispatches.Count - live, conflicts);
    }

    private static int ComparePrecedence(Selection left, Selection right)
    {
        var receiptOrder = (left.Dispatch.ContextPackageReceipt is not null)
            .CompareTo(right.Dispatch.ContextPackageReceipt is not null);
        if (receiptOrder != 0)
            return receiptOrder;

        var liveOrder = (left.ArchivedAt is null).CompareTo(right.ArchivedAt is null);
        // This archive format has no snapshot revision, so use the snapshot timestamp.
        return liveOrder != 0 ? liveOrder : Nullable.Compare(left.ArchivedAt, right.ArchivedAt);
    }

    private static bool SameUsage(Selection left, Selection right)
    {
        var first = left.Dispatch;
        var second = right.Dispatch;
        var firstUsage = DispatchUsageValues.RecordedUsage(first);
        var secondUsage = DispatchUsageValues.RecordedUsage(second);
        return left.Role == right.Role &&
            first.DispatchedAt == second.DispatchedAt &&
            first.ModelName == second.ModelName &&
            firstUsage?.InputTokens == secondUsage?.InputTokens &&
            firstUsage?.CachedInputTokens == secondUsage?.CachedInputTokens &&
            firstUsage?.OutputTokens == secondUsage?.OutputTokens &&
            DispatchUsageValues.RecordedTurns(first) == DispatchUsageValues.RecordedTurns(second) &&
            DispatchUsageValues.ReportedCount(DispatchUsageValues.RecordedToolCalls(first)) ==
                DispatchUsageValues.ReportedCount(DispatchUsageValues.RecordedToolCalls(second)) &&
            first.ContextPackageReceipt?.ModelInputTokenEstimate == second.ContextPackageReceipt?.ModelInputTokenEstimate;
    }

    private sealed record Selection(AgentRole Role, TaskDispatchRecord Dispatch, DateTimeOffset? ArchivedAt,
        bool HasLive, bool Conflicting = false);
}
