using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

/// <summary>Quotes list items from relevant root guidance headings, ignoring fenced examples.</summary>
internal static class GuidanceConventionReader
{
    private static readonly Regex Heading = new(@"^ {0,3}#{1,6}(?:[ \t]+|$)(.*?)(?:[ \t]+#+[ \t]*)?$", RegexOptions.CultureInvariant);
    private static readonly Regex Item = new(@"^[ \t]*(?:[-*+]|\d+[.)])[ \t]+(.*)$", RegexOptions.CultureInvariant);
    private static readonly string[] HeadingTerms = ["convention", "rule", "style", "testing", "build"];

    public static IReadOnlyList<ProjectConvention> Read(string root, List<ProjectOwnerQuestion> questions,
        DiscoveryTrackedPaths tracked)
    {
        var result = new List<ProjectConvention>();
        foreach (var name in new[] { "AGENTS.md", "CLAUDE.md", "README.md" })
        {
            var path = Path.Combine(root, name);
            if (!File.Exists(path) || !tracked.IncludesFile(path) || !ProjectFactHelpers.IsSafePath(root, path))
                continue;
            string[] lines;
            try
            {
                lines = File.ReadAllLines(path);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                ProjectFactHelpers.Ask($"conventions/{name}", $"Guidance could not be read: {exception.Message}",
                    new FactSource(name, 1), questions);
                continue;
            }

            string? heading = null;
            char fence = '\0';
            var fenceLength = 0;
            for (var index = 0; index < lines.Length; index++)
            {
                var line = lines[index];
                var trimmed = line.TrimStart();
                var markerLength = FenceLength(trimmed);
                if (fence != '\0')
                {
                    if (markerLength >= fenceLength && trimmed[0] == fence &&
                        trimmed[markerLength..].Trim().Length == 0)
                        fence = '\0';
                    continue;
                }
                if (markerLength >= 3)
                {
                    fence = trimmed[0];
                    fenceLength = markerLength;
                    continue;
                }
                var header = Heading.Match(line);
                if (header.Success)
                {
                    heading = header.Groups[1].Value.Trim();
                    continue;
                }
                var item = Item.Match(line);
                if (heading is null || !HeadingTerms.Any(term => heading.Contains(term, StringComparison.OrdinalIgnoreCase)) || !item.Success)
                    continue;
                var source = new FactSource(name, index + 1);
                var rule = item.Groups[1].Value.TrimEnd();
                while (index + 1 < lines.Length && !string.IsNullOrWhiteSpace(lines[index + 1]) &&
                       !Heading.IsMatch(lines[index + 1]) && !Item.IsMatch(lines[index + 1]) &&
                       FenceLength(lines[index + 1].TrimStart()) < 3)
                    rule += "\n" + lines[++index].Trim();
                if (string.IsNullOrWhiteSpace(rule))
                    continue;
                // Quoting an existing rule is medium confidence, without asking the owner to reconfirm each quote.
                result.Add(new ProjectConvention(name, heading, rule,
                    new ProjectFact<string>(rule, source, FactConfidence.Medium)));
            }
        }
        return result;
    }

    private static int FenceLength(string text)
    {
        if (text.Length == 0 || text[0] is not ('`' or '~'))
            return 0;
        var length = 1;
        while (length < text.Length && text[length] == text[0])
            length++;
        return length;
    }
}
