using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class AuthorStaleRetirement
{
    internal const string Tag = "author-refused";

    private static readonly Regex CommitReference = new(
        @"(?<![0-9A-Fa-f])[0-9A-Fa-f]{7,40}(?![0-9A-Fa-f])", RegexOptions.CultureInvariant);

    internal static void Retire(BacklogStore store, string itemId, string reason,
        IReadOnlyList<string> evidence, Func<string, bool> isAncestorOfMain,
        CancellationToken cancellationToken = default)
    {
        var note = reason + (evidence.Count == 0 ? "" : Environment.NewLine + "Evidence:" +
            Environment.NewLine + string.Join(Environment.NewLine, evidence.Select(reference => "- " + reference)));
        // The draft's read-only snapshot omits tags; use the current item returned by the write.
        var current = store.AppendNoteAsync(itemId, note, cancellationToken).GetAwaiter().GetResult();
        if (current.Status != BacklogItemStatus.Open) return;

        var tags = current.Tags.Split([',', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        var refused = tags.Contains(Tag, StringComparer.OrdinalIgnoreCase);
        var candidates = new[] { reason }.Concat(evidence)
            .SelectMany(text => CommitReference.Matches(text).Select(match => match.Value.ToLowerInvariant()))
            .Distinct(StringComparer.Ordinal);
        if (candidates.Any(isAncestorOfMain))
        {
            store.CloseAsync(itemId, reason, cancellationToken).GetAwaiter().GetResult();
            // Reopening a closed item must not require a separate refusal-tag removal.
            if (refused)
                store.UpdateAsync(itemId, new(Tags: string.Join(",",
                    tags.Where(tag => !tag.Equals(Tag, StringComparison.OrdinalIgnoreCase)))),
                    cancellationToken).GetAwaiter().GetResult();
        }
        else if (!refused)
        {
            store.UpdateAsync(itemId, new(Tags: string.IsNullOrWhiteSpace(current.Tags)
                ? Tag : current.Tags + "," + Tag), cancellationToken).GetAwaiter().GetResult();
        }
    }
}
