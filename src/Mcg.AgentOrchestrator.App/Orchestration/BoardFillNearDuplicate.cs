using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record BoardFillNearDuplicateReference(string Kind, string Id, string Name);
internal sealed record BoardFillDuplicateSource(string Id, string Title, string Body);

// A lexical match requires both candidate-title coverage and a shared repository file.
internal static class BoardFillNearDuplicate
{
    internal const double MinimumOverlap = 0.5;
    internal const int MinimumTokenLength = 3;
    internal const string NotePrefix = "[board-fill-near-duplicate kind=";
    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "a", "an", "and", "are", "as", "at", "be", "been", "being", "by", "for", "from",
        "in", "into", "is", "it", "of", "on", "or", "that", "the", "their", "this", "to",
        "was", "were", "will", "with"
    };
    private static readonly Regex Words = new(@"[\p{L}\p{Nd}]+", RegexOptions.CultureInvariant);
    private static readonly Regex PathTokens = new("[^\\s`\"'<>\\[\\](){};,|]+", RegexOptions.CultureInvariant);
    private static readonly Regex CitationSuffix = new(
        @"(?::\d+(?:-\d+)?(?::\d+)?|#L\d+(?:-L?\d+)?|::[\p{L}_][\p{L}\p{N}_:.]*)$",
        RegexOptions.CultureInvariant);
    private static readonly Regex RelativeFile = new(
        @"^(?:[\p{L}\p{N}_@.\-]+/)*[\p{L}\p{N}_@.\-][\p{L}\p{N}_@.\-]*\.[\p{L}][\p{L}\p{N}_\-]*$",
        RegexOptions.CultureInvariant);

    internal static HashSet<string> RecentItemIds(IReadOnlyList<BoardFillDraftRound> rounds, DateTimeOffset now) =>
        rounds.Where(round => round.StartedAt >= now.AddHours(-24) && round.StartedAt <= now)
            .Select(round => round.BacklogItemId).ToHashSet(StringComparer.Ordinal);

    internal static Func<BacklogItem, BoardFillNearDuplicateReference?> Lookup(
        IReadOnlyList<BacklogItem> items, IReadOnlyList<Goal> goals,
        IReadOnlyList<BoardFillDraftRound> rounds, DateTimeOffset now)
    {
        var recentIds = RecentItemIds(rounds, now);
        return LookupSources(items.Where(item => recentIds.Contains(item.Id))
            .Select(item => new BoardFillDuplicateSource(item.Id, item.Title, item.Body)), goals);
    }

    internal static Func<BacklogItem, BoardFillNearDuplicateReference?> LookupSources(
        IEnumerable<BoardFillDuplicateSource> sources, IReadOnlyList<Goal> goals)
    {
        var references = goals.Where(goal => !goal.IsTerminal)
            .Select(goal => (Reference: new BoardFillNearDuplicateReference("goal", goal.Id.Value, GoalTitle(goal.Objective)),
                Text: goal.Objective))
            .OrderBy(entry => entry.Reference.Id, StringComparer.Ordinal)
            .Concat(sources
                .DistinctBy(item => item.Id, StringComparer.Ordinal)
                .OrderBy(item => item.Id, StringComparer.Ordinal)
                .Select(item => (Reference: new BoardFillNearDuplicateReference("item", item.Id, item.Title),
                    Text: item.Title + "\n" + item.Body)))
            .Select(entry => (entry.Reference, Words: TitleWords(entry.Reference.Name), Paths: CitedPaths(entry.Text)))
            .ToArray();

        return candidate =>
        {
            var words = TitleWords(candidate.Title);
            var paths = CitedPaths(candidate.Title + "\n" + candidate.Body);
            if (words.Count == 0 || paths.Count == 0) return null;
            foreach (var reference in references)
            {
                if (reference.Reference.Kind == "item" && reference.Reference.Id == candidate.Id) continue;
                if (reference.Words.Count != 0 && paths.Overlaps(reference.Paths) &&
                    words.Count(reference.Words.Contains) / (double)words.Count >= MinimumOverlap)
                    return reference.Reference;
            }
            return null;
        };
    }

    internal static HashSet<string> TitleWords(string title) => Words.Matches(title)
        .Select(match => match.Value.ToLowerInvariant())
        .Where(word => word.Length >= MinimumTokenLength && !StopWords.Contains(word))
        .ToHashSet(StringComparer.Ordinal);

    internal static HashSet<string> CitedPaths(string text)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match token in PathTokens.Matches(text))
        {
            var path = token.Value.TrimStart('*').TrimEnd('*', '.', '!', '?', ':').Replace('\\', '/');
            path = CitationSuffix.Replace(path, "");
            if (path.StartsWith("./", StringComparison.Ordinal)) path = path[2..];
            // Match the whole token so URLs and absolute paths cannot yield relative suffixes.
            if (path.Split('/').Contains("..", StringComparer.Ordinal) || !RelativeFile.IsMatch(path)) continue;
            paths.Add(path);
        }
        return paths;
    }

    internal static string GoalTitle(string objective)
    {
        var lines = objective.Split('\n').Select(line => line.Trim()).Where(line => line.Length != 0).ToArray();
        foreach (var line in lines)
        {
            var heading = Regex.Match(line, @"^#{1,6}\s+(.+)$", RegexOptions.CultureInvariant);
            if (heading.Success && heading.Groups[1].Value.Trim().TrimEnd('#').Trim() is { Length: > 0 } title)
                return title;
        }
        return lines.FirstOrDefault() ?? "";
    }

    internal static string Token(BoardFillNearDuplicateReference reference) =>
        $"{NotePrefix}{reference.Kind} id={reference.Id}]";

    internal static string Note(BoardFillNearDuplicateReference reference)
    {
        var name = reference.Name.Replace('\r', ' ').Replace('\n', ' ');
        return $"{Token(reference)} Board fill skipped: near-duplicate of {reference.Kind} {reference.Id}: " +
            name[..Math.Min(200, name.Length)];
    }
}
