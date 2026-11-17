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
            string? paragraph = null;
            string? listLeadIn = null;
            var previousLineWasProse = false;
            var inList = false;
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
                    paragraph = listLeadIn = null;
                    previousLineWasProse = inList = false;
                    continue;
                }
                var header = Heading.Match(line);
                if (header.Success)
                {
                    heading = header.Groups[1].Value.Trim();
                    paragraph = listLeadIn = null;
                    previousLineWasProse = inList = false;
                    continue;
                }
                var item = Item.Match(line);
                if (string.IsNullOrWhiteSpace(line))
                {
                    previousLineWasProse = false;
                    continue;
                }
                if (!item.Success)
                {
                    // Non-list prose includes blockquotes and tables; keep its text without interpreting it.
                    paragraph = previousLineWasProse ? paragraph + " " + line.Trim() : line.Trim();
                    previousLineWasProse = true;
                    inList = false;
                    listLeadIn = null;
                    continue;
                }
                if (!inList)
                {
                    listLeadIn = paragraph;
                    inList = true;
                }
                paragraph = null;
                previousLineWasProse = false;
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
                if (listLeadIn is not null)
                    rule = listLeadIn + "\n" + rule;
                // Quoting an existing rule is medium confidence, without asking the owner to reconfirm each quote.
                result.Add(new ProjectConvention(name, heading, rule,
                    new ProjectFact<string>(rule, source, FactConfidence.Medium)));
            }
        }
        var indices = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var firstOccurrences = new List<ProjectConvention>();
        var sources = new List<List<FactSource>>();
        foreach (var convention in result)
        {
            var key = Regex.Replace(convention.Rule.Trim(), @"\s+", " ");
            if (!indices.TryGetValue(key, out var index))
            {
                indices.Add(key, firstOccurrences.Count);
                firstOccurrences.Add(convention);
                sources.Add([convention.Evidence.Source]);
            }
            else
                sources[index].Add(convention.Evidence.Source);
        }
        return firstOccurrences.Select((first, index) => new ProjectConvention(
            first.DocumentPath, first.Heading, first.Rule, first.Evidence, sources[index])).ToArray();
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
