using System.Text.RegularExpressions;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record AuthorBriefDraftProvidedPremise(int Start, int End, string GoalPrefix)
{
    private static readonly Regex Boundaries = new(@"^[ \t]*(?:(?<bullet>[-*][ \t]+)|(?=\r?$))",
        RegexOptions.Multiline | RegexOptions.CultureInvariant);

    internal static bool IsGoalPrefix(string? value) =>
        value is { Length: >= 6 and <= 32 } && value.All(char.IsAsciiHexDigit);

    internal bool Contains(int index) => index >= Start && index < End;

    internal bool IsListed(IReadOnlyList<string> goals) =>
        goals.Contains(GoalPrefix, StringComparer.OrdinalIgnoreCase);

    internal static IReadOnlyList<AuthorBriefDraftProvidedPremise> Find(string text)
    {
        var boundaries = Boundaries.Matches(text).Cast<Match>().ToArray();
        var premises = new List<AuthorBriefDraftProvidedPremise>();
        for (var index = 0; index < boundaries.Length; index++)
        {
            var boundary = boundaries[index];
            if (!boundary.Groups["bullet"].Success) continue;
            var contentStart = boundary.Index + boundary.Length;
            var lineEnd = text.IndexOf('\n', contentStart);
            var content = text[contentStart..(lineEnd < 0 ? text.Length : lineEnd)];
            const string marker = "provided-by-";
            if (!content.StartsWith(marker, StringComparison.Ordinal)) continue;
            var colon = content.IndexOf(':', marker.Length);
            if (colon < 0) continue;
            var prefix = content[marker.Length..colon];
            if (!IsGoalPrefix(prefix)) continue;
            var end = index + 1 < boundaries.Length ? boundaries[index + 1].Index : text.Length;
            premises.Add(new(boundary.Index, end, prefix));
        }
        return premises;
    }
}
