using System.Text.RegularExpressions;

namespace Mcg.AgentOrchestrator.Core;

/// <summary>Only volatile tokens are masked; wording, case and punctuation remain significant.</summary>
public static class FindingDescriptionNormalizer
{
    private static readonly Regex Tokens = new(
        @"(?<timestamp>\b\d{4}-\d{2}-\d{2}(?:[T ]\d{2}:\d{2}:\d{2}(?:\.\d+)?(?:Z|[+-]\d{2}:\d{2})?)?\b)|" +
        @"(?<path>(?:[A-Za-z]:[\\/]|\\\\|\.{0,2}/)?(?:[\w.@-]+[\\/])+[\w@-](?:[\w.@-]*[\w@-])?|\b[\w-]+\.(?:csproj|cs|sln|json|md|txt|ps1|yaml|yml|xml|log)\b)|" +
        @"(?<number>\b\d+(?:\.\d+)?\b)|(?<sha>\b[0-9a-fA-F]{7,64}\b)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static string Normalize(string description)
    {
        ArgumentNullException.ThrowIfNull(description);
        var collapsed = Whitespace.Replace(description, " ").Trim();
        // One pass prevents masks from consuming one another or leaving partial tokens behind.
        return Tokens.Replace(collapsed, match => match.Groups["timestamp"].Success ? "<timestamp>"
            : match.Groups["path"].Success ? "<path>"
            : match.Groups["sha"].Success ? "<sha>" : "<number>");
    }
}
