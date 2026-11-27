using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class AttentionReadGoalSelection
{
    internal sealed record Result(bool Decline, IReadOnlyList<GoalId> GoalIds);

    private static bool IsAttentionAllFlag(string value) =>
        value.Equals("--all", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("--include-parked", StringComparison.OrdinalIgnoreCase);

    internal static (bool IncludeHistory, string? GoalPrefix, AttentionHistoryLimit Limit) ParseShowArgs(
        IReadOnlyList<string> parts)
    {
        var args = new List<string>();
        AttentionHistoryLimit? overrideLimit = null;
        for (var index = 2; index < parts.Count; index++)
        {
            var arg = parts[index];
            if (arg.Equals("--limit", StringComparison.OrdinalIgnoreCase))
            {
                if (overrideLimit is not null || index + 1 == parts.Count)
                    throw new ArgumentException(CliCommandHelp.AttentionShowUsage);

                overrideLimit = AttentionHistoryLimit.Parse(parts[++index]);
            }
            else if (arg.StartsWith("--limit=", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException(CliCommandHelp.AttentionShowUsage);
            }
            else if (!string.IsNullOrWhiteSpace(arg))
            {
                args.Add(arg);
            }
        }

        var includeHistory = args.RemoveAll(IsAttentionAllFlag) > 0;
        if (overrideLimit is not null && !includeHistory)
            throw new ArgumentException(CliCommandHelp.AttentionShowUsage);

        var limit = overrideLimit ?? AttentionHistoryLimit.Default;
        if (args.Count == 0)
        {
            return (includeHistory, null, limit);
        }

        if (args[0].Equals("--goal", StringComparison.OrdinalIgnoreCase))
        {
            if (args.Count < 2)
            {
                throw new ArgumentException(CliCommandHelp.AttentionShowUsage);
            }

            return (includeHistory, args[1], limit);
        }

        return (includeHistory, args[0], limit);
    }

    internal static IReadOnlyList<GoalId>? ReadPostLandingDutyGoalIds(
        IOrchestratorStateQueries queries, IReadOnlyList<GoalSummary> metadata, AttentionReadProjection? projection)
    {
        if (projection is not null)
        {
            var ownerQuestionGoals = projection.Goals
                .Where(goal => goal.Status is not (GoalStatus.Parked or GoalStatus.Completed or GoalStatus.Cancelled or GoalStatus.Superseded))
                .Select(goal => goal.GoalId).ToHashSet(StringComparer.OrdinalIgnoreCase);
            return (projection.PostLandingDuties ?? []).Select(row => new GoalId(row.GoalId))
                .Concat((projection.EnvironmentHolds ?? []).Select(row => new GoalId(row.GoalId)))
                .Concat(projection.OwnerQuestions.Where(row => ownerQuestionGoals.Contains(row.GoalId))
                    .Select(row => new GoalId(row.GoalId))).Distinct().ToArray();
        }

        // Null means unsupported, not empty. Explicit IDs hydrate archived snapshots too.
        var ids = metadata.Select(goal => new GoalId(goal.Id)).ToArray();
        if (ids.Length == 0) return [];
        var kernel = queries.LoadGoalsAsync(ids).GetAwaiter().GetResult();
        if (kernel.Goals.Count != ids.Length || !ids.ToHashSet().SetEquals(kernel.Goals.Select(goal => goal.Id)))
            return null;
        return kernel.Goals.Where(goal => goal.OutstandingPostLandingDuties.Count > 0 || goal.CurrentHold?.State == EnvironmentHold.State ||
                (goal.Status is not (GoalStatus.Parked or GoalStatus.Completed or GoalStatus.Cancelled or GoalStatus.Superseded) &&
                 OwnerQuestionHoldBlocker.TryRead(goal) is not null))
            .Select(goal => goal.Id).ToArray();
    }

    internal static Goal Resolve(AgentOrchestratorKernel kernel, string goalPrefix)
    {
        var matches = kernel.Goals
            .Where(goal => goal.Id.Value.StartsWith(goalPrefix, StringComparison.OrdinalIgnoreCase))
            .ToList();

        return matches.Count switch
        {
            1 => matches[0],
            0 => throw new KeyNotFoundException($"No goal found matching prefix '{goalPrefix}'."),
            _ => throw new InvalidOperationException($"Goal prefix '{goalPrefix}' is ambiguous ({matches.Count} matches).")
        };
    }

    internal static Result Select(
        IReadOnlyList<GoalSummary> metadata,
        IReadOnlyList<HumanInputRequestSnapshot> openWaits,
        IReadOnlyList<CollaborationItem> storedItems,
        IReadOnlyList<CollaborationItem> attentionQueue,
        IReadOnlyList<GoalId>? postLandingDutyGoalIds = null)
    {
        var goals = metadata.ToDictionary(goal => goal.Id, StringComparer.OrdinalIgnoreCase);
        var parked = metadata
            .Where(goal => goal.Status.Equals(nameof(GoalStatus.Parked), StringComparison.OrdinalIgnoreCase))
            .Select(goal => goal.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (openWaits.Any(wait => parked.Contains(wait.GoalId)) ||
            storedItems.Any(item => item.GoalId is not null && parked.Contains(item.GoalId) &&
                item.Type is CollaborationItemType.Decision or CollaborationItemType.Clarification or CollaborationItemType.Verify &&
                item.Status is not CollaborationItemStatus.Resolved and not CollaborationItemStatus.Closed))
            return new Result(true, []);

        string[] nonLiveStatuses =
        [
            nameof(GoalStatus.Parked), nameof(GoalStatus.Completed), nameof(GoalStatus.Failed),
            nameof(GoalStatus.Cancelled), nameof(GoalStatus.Superseded)
        ];
        var ids = openWaits.Select(wait => wait.GoalId)
            .Concat(attentionQueue
                .Where(item => CollaborationItemLifecycle.IsReachUpType(item.Type) && item.GoalId is not null)
                .Select(item => item.GoalId!))
            .Where(id => goals.TryGetValue(id, out var goal) &&
                !nonLiveStatuses.Contains(goal.Status, StringComparer.OrdinalIgnoreCase))
            .Concat(openWaits.Where(wait => goals.TryGetValue(wait.GoalId, out var goal) &&
                PostLandingFollowUpAttention.IsFollowUp(goal, wait)).Select(wait => wait.GoalId))
            .Concat((postLandingDutyGoalIds ?? []).Select(id => id.Value).Where(goals.ContainsKey))
            .Select(id => goals[id].Id)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(id => id, StringComparer.Ordinal)
            .Select(id => new GoalId(id))
            .ToArray();
        return new Result(false, ids);
    }
}

internal static class AttentionShowParkedState
{
    private static string ParkedAttentionResolution(Goal goal) => goal.Timeline
        .LastOrDefault(evt => evt.Kind == ProgressKind.GoalPolicyDecision &&
            GoalParkResolution.IsParkResolution(evt.Message))?.Message ?? GoalParkResolution.Fallback;

    internal static int ResolveOpenItems(AgentOrchestratorKernel kernel, CollaborationItemStore store)
    {
        var resolved = 0;
        foreach (var goal in kernel.Goals.Where(goal => goal.Status == GoalStatus.Parked))
            resolved += store.ResolveOpenForGoalAsync(goal.Id.Value, ParkedAttentionResolution(goal)).GetAwaiter().GetResult();
        return resolved;
    }

    internal static Dictionary<string, string> ReadResolutions(AgentOrchestratorKernel kernel)
    {
        var resolutions = kernel.Goals.Where(goal => goal.Status == GoalStatus.Parked)
            .ToDictionary(goal => goal.Id.Value, ParkedAttentionResolution, StringComparer.Ordinal);
        var waitingGoals = kernel.Goals.Where(goal => goal.Status == GoalStatus.WaitingForHuman)
            .Select(goal => goal.Id).ToHashSet();
        if (waitingGoals.Count == 0)
            return resolutions;

        foreach (var wait in kernel.HumanInputRequests
            .Where(wait => waitingGoals.Contains(wait.GoalId) && !wait.IsCompleted &&
                GoalParkResolution.IsParkResolution(wait.Question))
            .OrderByDescending(wait => wait.RequestedAt))
        {
            resolutions.TryAdd(wait.GoalId.Value, wait.Question);
        }

        return resolutions;
    }

    internal static IReadOnlyList<HumanInputRequest> ProjectWaits(
        IReadOnlyList<HumanInputRequest> waits, AgentOrchestratorKernel kernel,
        IReadOnlyDictionary<string, string> parkedResolutions, DateTimeOffset now)
    {
        if (parkedResolutions.Count == 0)
            return waits;

        return waits.Select(wait =>
        {
            if (wait.IsCompleted || !HumanWaitPolicyDefaults.BlocksActiveWork(wait.Kind) ||
                !parkedResolutions.TryGetValue(wait.GoalId.Value, out var resolution))
                return wait;

            var displayWait = HumanInputRequest.FromSnapshot(wait.ToSnapshot());
            displayWait.Complete(resolution, now, briefVersion: kernel.GetGoal(wait.GoalId).AuthoritativeBrief.Version);
            return displayWait;
        }).ToArray();
    }

    internal static IReadOnlyList<CollaborationItem> ProjectItems(
        IReadOnlyList<CollaborationItem> items, IReadOnlyDictionary<string, string> parkedResolutions, DateTimeOffset now)
    {
        if (parkedResolutions.Count == 0)
            return items;

        return items.Select(item => item.GoalId is not null && !CollaborationItemLifecycle.IsTerminal(item.Status) &&
            parkedResolutions.TryGetValue(item.GoalId, out var resolution)
                ? item with { Status = CollaborationItemStatus.Resolved, Resolution = resolution, ResolvedAt = now }
                : item).ToArray();
    }

    internal static void PrintDecisions(
        OrchestratorWorkspace workspace, IReadOnlyCollection<Goal> goals, CollaborationItemStore store,
        IReadOnlyDictionary<string, string> parkedResolutions, DateTimeOffset now)
    {
        var resolvedIds = parkedResolutions.Count == 0 ? [] : store.GetAttentionQueueAsync().GetAwaiter().GetResult()
            .Where(item => item.GoalId is not null && parkedResolutions.ContainsKey(item.GoalId))
            .Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        if (resolvedIds.Count == 0)
        {
            AttentionShowDecisionsTextView.PrintThenDuties(workspace, goals);
            return;
        }

        // This view sees the resolutions the writer fallback previously recorded during show.
        var decisions = OwnerDecisionReader.ReadAsync(OwnerQuestionReadModelFactory.ForWorkspace(workspace))
            .GetAwaiter().GetResult();
        var entries = decisions.Entries.Where(entry => !resolvedIds.Contains(entry.ItemId)).ToArray();
        Console.WriteLine($"Decisions ({entries.Length}):");
        if (entries.Length == 0)
            Console.WriteLine("  none");
        foreach (var entry in entries)
        {
            TimeSpan? age = entry.RaisedAt is { } raisedAt
                ? TimeSpan.FromTicks(Math.Max(0, (now - raisedAt).Ticks)) : null;
            var text = entry.Text.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\n", "\n    ");
            Console.WriteLine($"  [{entry.Kind}] {entry.GoalPrefix} item={entry.ItemId} age={OwnerConsoleBoardRenderer.Age(age)}: {text}");
        }

        var hidden = decisions.Snapshot.Hidden.Where(item => !resolvedIds.Contains(item.Question.ItemId)).ToArray();
        var reasons = string.Join(", ", hidden.GroupBy(item => item.Reason)
            .OrderBy(group => group.Key, StringComparer.Ordinal).Select(group => $"{group.Key}={group.Count()}"));
        Console.WriteLine($"Hidden decisions: {hidden.Length}{(hidden.Length == 0 ? "" : $" ({reasons})")}");
        Console.WriteLine();
        PostLandingDutiesTextView.Print(goals);
    }
}
