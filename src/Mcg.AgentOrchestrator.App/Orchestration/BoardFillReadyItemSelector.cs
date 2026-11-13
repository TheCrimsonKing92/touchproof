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
                note.Text.Contains(marker, StringComparison.OrdinalIgnoreCase)));

    internal static bool IsAuthorRefused(BacklogItem item) => HasTag(item, AuthorStaleRetirement.Tag);

    private static bool HasTag(BacklogItem item, string tag) =>
        item.Tags.Split([',', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Contains(tag, StringComparer.OrdinalIgnoreCase);

    internal static BacklogItem? Select(IReadOnlyList<BacklogItem> items, IReadOnlyList<Goal> goals,
        IReadOnlySet<string> alreadyDrafted, Func<BacklogItem, BacklogReadiness> readiness,
        IReadOnlyList<BoardFillDraftRound>? rounds = null, IReadOnlyList<string>? preferredOrder = null,
        string? currentMainHead = null, Func<string, string, IReadOnlyList<string>?>? changedFiles = null)
    {
        var ranks = new Dictionary<string, int>(StringComparer.Ordinal);
        if (preferredOrder is not null)
            for (var index = 0; index < preferredOrder.Count; index++)
                ranks.TryAdd(preferredOrder[index], index);

        return items.Where(item => item.Status == BacklogItemStatus.Open &&
            !alreadyDrafted.Contains(item.Id) &&
            (rounds is null || rounds.Count(round => round.BacklogItemId == item.Id &&
                BoardFillFailureKind.CountsTowardItem(round) && round.ChangeStamp >= ChangeStamp(item)) < MaxFailedRoundsPerChange) &&
            !goals.Any(goal => goal.SourceBacklogItemId == item.Id && !goal.IsTerminal) &&
            !IsOwnerGated(item) && !IsAuthorRefused(item) &&
            !BoardFillContradictionHold.Blocks(item, currentMainHead, changedFiles))
        .Select(item => (Item: item, Ready: readiness(item)))
        .Where(candidate => candidate.Ready.Eligible)
        .OrderBy(candidate => ranks.GetValueOrDefault(candidate.Item.Id, int.MaxValue))
        .ThenByDescending(candidate => !string.IsNullOrWhiteSpace(candidate.Item.Priority))
        .ThenByDescending(candidate => candidate.Ready.HasLandedDependency)
        .ThenByDescending(candidate => candidate.Item.UpdatedAt)
        .ThenBy(candidate => candidate.Item.Id, StringComparer.Ordinal)
        .Select(candidate => candidate.Item).FirstOrDefault();
    }
}
