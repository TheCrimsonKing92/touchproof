using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class BoardFillReadyItemSelector
{
    internal const int MaxFailedRoundsPerChange = 2;

    private static readonly string[] OwnerMarkers =
        ["owner decision", "owner approval", "owner ruling", "approve-policy-change", "decision for you"];

    internal static DateTimeOffset ChangeStamp(BacklogItem item)
    {
        var marker = BoardFillContradictionHold.NewestMarker(item);
        // AppendNoteAsync also bumps UpdatedAt to the marker's time. Preserve the pre-write stamp.
        var updated = marker is null || item.UpdatedAt > marker.CreatedAt ? item.UpdatedAt :
            BoardFillContradictionHold.Parse(marker.Text)?.ItemChangeStamp ?? item.CreatedAt;
        return item.Notes.Where(note => !BoardFillContradictionHold.IsMarker(note.Text))
            .Select(note => note.CreatedAt).Append(updated).Max();
    }

    internal static bool IsOwnerGated(BacklogItem item) =>
        HasTag(item, "owner-gated") ||
        OwnerMarkers.Any(marker => item.Body.Contains(marker, StringComparison.OrdinalIgnoreCase) ||
            item.Notes.Any(note => !BoardFillContradictionHold.IsMarker(note.Text) &&
                !note.Text.StartsWith(BoardFillNearDuplicate.NotePrefix, StringComparison.Ordinal) &&
                note.Text.Contains(marker, StringComparison.OrdinalIgnoreCase)));

    internal static bool IsAuthorRefused(BacklogItem item) => HasTag(item, AuthorStaleRetirement.Tag);

    internal static bool HasTag(BacklogItem item, string tag) =>
        item.Tags.Split([',', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Contains(tag, StringComparer.OrdinalIgnoreCase);

    internal static BacklogItem? Select(IReadOnlyList<BacklogItem> items, IReadOnlyList<Goal> goals,
        IReadOnlySet<string> alreadyDrafted, Func<BacklogItem, BacklogReadiness> readiness,
        IReadOnlyList<BoardFillDraftRound>? rounds = null, IReadOnlyList<string>? preferredOrder = null,
        string? currentMainHead = null, Func<string, string, IReadOnlyList<string>?>? changedFiles = null,
        IReadOnlySet<string>? epicMembers = null, Action<BacklogItem>? epicMissing = null,
        Func<BacklogItem, BoardFillNearDuplicateReference?>? nearDuplicate = null,
        Action<BacklogItem, BoardFillNearDuplicateReference>? nearDuplicateSkipped = null) =>
        SelectReady(EvaluateCandidates(items, goals, alreadyDrafted, readiness, rounds, currentMainHead, changedFiles),
            preferredOrder, epicMembers, epicMissing, nearDuplicate, nearDuplicateSkipped);

    internal static IEnumerable<(BacklogItem Item, BacklogReadiness Ready)> EvaluateCandidates(
        IReadOnlyList<BacklogItem> items, IReadOnlyList<Goal> goals, IReadOnlySet<string> alreadyDrafted,
        Func<BacklogItem, BacklogReadiness> readiness, IReadOnlyList<BoardFillDraftRound>? rounds = null,
        string? currentMainHead = null, Func<string, string, IReadOnlyList<string>?>? changedFiles = null)
    {
        var activeSources = goals.Where(goal => !goal.IsTerminal).Select(goal => goal.SourceBacklogItemId)
            .ToHashSet(StringComparer.Ordinal);
        var failures = rounds?.Where(BoardFillFailureKind.CountsTowardItem).ToLookup(round => round.BacklogItemId,
            StringComparer.Ordinal);
        return items.Where(item => item.Status == BacklogItemStatus.Open &&
            !alreadyDrafted.Contains(item.Id) &&
            (failures is null || failures[item.Id].Count(round => round.ChangeStamp >= ChangeStamp(item)) < MaxFailedRoundsPerChange) &&
            !activeSources.Contains(item.Id) &&
            !IsOwnerGated(item) && !IsAuthorRefused(item) &&
            !BoardFillContradictionHold.Blocks(item, currentMainHead, changedFiles))
            .DistinctBy(item => item.Id, StringComparer.Ordinal)
            .Select(item => (Item: item, Ready: readiness(item)));
    }

    internal static BacklogItem? SelectReady(IEnumerable<(BacklogItem Item, BacklogReadiness Ready)> candidates,
        IReadOnlyList<string>? preferredOrder, IReadOnlySet<string>? epicMembers, Action<BacklogItem>? epicMissing,
        Func<BacklogItem, BoardFillNearDuplicateReference?>? nearDuplicate,
        Action<BacklogItem, BoardFillNearDuplicateReference>? nearDuplicateSkipped)
    {
        var ranks = new Dictionary<string, int>(StringComparer.Ordinal);
        if (preferredOrder is not null)
            for (var index = 0; index < preferredOrder.Count; index++)
                ranks.TryAdd(preferredOrder[index], index);

        return candidates
        .Where(candidate => candidate.Ready.Eligible)
        .Where(candidate =>
        {
            if (epicMembers is null || epicMembers.Contains(candidate.Item.Id)) return true;
            epicMissing?.Invoke(candidate.Item);
            return false;
        })
        .Where(candidate =>
        {
            var reference = nearDuplicate?.Invoke(candidate.Item);
            if (reference is null) return true;
            nearDuplicateSkipped?.Invoke(candidate.Item, reference);
            return false;
        })
        .OrderBy(candidate => candidate.Item.PinnedAt is null)
        .ThenBy(candidate => candidate.Item.PinnedAt)
        .ThenBy(candidate => ranks.GetValueOrDefault(candidate.Item.Id, int.MaxValue))
        .ThenBy(candidate => BacklogPriorityLevels.Rank(candidate.Item.Priority))
        .ThenByDescending(candidate => candidate.Ready.HasLandedDependency)
        .ThenByDescending(candidate => candidate.Item.UpdatedAt)
        .ThenBy(candidate => candidate.Item.Id, StringComparer.Ordinal)
        .Select(candidate => candidate.Item).FirstOrDefault();
    }
}
