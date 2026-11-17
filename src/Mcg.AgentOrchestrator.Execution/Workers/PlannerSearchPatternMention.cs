using System.Text.RegularExpressions;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal static partial class PlannerSearchPatternMention
{
    internal static bool IsSearchPatternMention(string plan, int spanStart)
    {
        if ((uint)spanStart >= (uint)plan.Length || plan[spanStart] != '`')
        {
            return false;
        }

        var lineStart = spanStart;
        while (lineStart > 0 && plan[lineStart - 1] is not ('\r' or '\n'))
        {
            lineStart--;
        }

        var lineEnd = spanStart;
        while (lineEnd < plan.Length && plan[lineEnd] is not ('\r' or '\n'))
        {
            lineEnd++;
        }

        var line = plan[lineStart..lineEnd];
        var isWildcardSpan = false;
        foreach (Match match in BacktickedCitation().Matches(line))
        {
            if (match.Index == spanStart - lineStart)
            {
                isWildcardSpan = match.Groups["citation"].Value.IndexOfAny(['*', '?']) >= 0;
                break;
            }
        }

        if (!isWildcardSpan)
        {
            return false;
        }

        // Both cue sets use prose only: code cannot supply a cue or join adjacent words.
        var prose = BacktickedCitation().Replace(line, " ");
        return PatternCue().IsMatch(prose) || HistoricalCue().IsMatch(prose);
    }

    [GeneratedRegex(@"`(?<citation>[^`\r\n]+)`")]
    private static partial Regex BacktickedCitation();

    [GeneratedRegex(@"\b(?:patterns?|globs?)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PatternCue();

    [GeneratedRegex(@"\b(?:rejected|superseded|replaced|previously|formerly|earlier[\t\p{Zs}]+draft)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HistoricalCue();
}
