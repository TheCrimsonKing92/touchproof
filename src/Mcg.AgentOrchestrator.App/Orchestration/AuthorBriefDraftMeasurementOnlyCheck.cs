using System.Text;
using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class AuthorBriefDraftMeasurementOnlyCheck
{
    internal const string Name = "measurement-only-draft";
    private static readonly Regex Instrumentation = new(
        @"\b(timer|stopwatch|counter|census|attribute|attribution|instrument|measure)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex BehaviourChange = new(
        @"\b(fix|reduce|skip|avoid|cache|remove|replace|batch|defer|cap|bound)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex Heading = new(@"^(#{1,6})(?:[ \t]+(.*?)\s*|)$", RegexOptions.CultureInvariant);
    private static readonly Regex NumberedItem = new(@"^([ \t]*)\d+[.)](?:[ \t]+|$)", RegexOptions.CultureInvariant);

    internal static bool IsMeasureTagged(string backlogStorePath, string itemId)
    {
        var item = BacklogStore.OpenReadOnly(backlogStorePath).GetByIdPrefixAsync(itemId).GetAwaiter().GetResult();
        return item is not null && BoardFillReadyItemSelector.HasTag(item, "measure");
    }

    internal static AuthorBriefDraftCheck Run(string markdown, bool measureTagged)
    {
        if (measureTagged) return new(Name, true, "The backlog item is tagged measure.");
        var items = BuildItems(markdown);
        if (items.Count == 0) return new(Name, true, "No numbered What to build items.");
        var measurementOnly = items.All(item => Instrumentation.IsMatch(item)) &&
            !items.Any(item => BehaviourChange.IsMatch(item));
        return new(Name, !measurementOnly, measurementOnly
            ? "Every numbered What to build item only adds instrumentation and none changes the behaviour it measures. " +
              "Propose the fix the measurement would inform, or return kind stale with staleKind refused " +
              "and a reason saying the item needs a fix design."
            : "At least one What to build item changes behaviour or is not instrumentation work.");
    }

    private static IReadOnlyList<string> BuildItems(string markdown)
    {
        var items = new List<string>();
        StringBuilder? current = null;
        var sectionLevel = 0;
        var itemIndent = int.MaxValue;
        char fenceCharacter = '\0';
        var fenceLength = 0;
        foreach (var line in markdown.ReplaceLineEndings("\n").Split('\n'))
        {
            var trimmed = line.TrimStart();
            var markerLength = trimmed.Length > 0 && trimmed[0] is '`' or '~'
                ? trimmed.TakeWhile(character => character == trimmed[0]).Count() : 0;
            if (fenceLength > 0)
            {
                // Fenced vocabulary belongs to the current item, but its Markdown markers do not.
                current?.AppendLine(line);
                if (markerLength >= fenceLength && trimmed[0] == fenceCharacter &&
                    string.IsNullOrWhiteSpace(trimmed[markerLength..])) fenceLength = 0;
                continue;
            }
            if (markerLength >= 3)
            {
                fenceCharacter = trimmed[0];
                fenceLength = markerLength;
                current?.AppendLine(line);
                continue;
            }

            var heading = Heading.Match(trimmed);
            if (heading.Success)
            {
                var level = heading.Groups[1].Length;
                if (sectionLevel > 0 && level <= sectionLevel) break;
                var title = Regex.Replace(heading.Groups[2].Value, @"[ \t]+#+$", "")
                    .Trim().TrimEnd(':').TrimEnd();
                if (sectionLevel == 0 && title.Equals("What to build", StringComparison.OrdinalIgnoreCase))
                    sectionLevel = level;
            }
            if (sectionLevel == 0) continue;

            var numbered = NumberedItem.Match(line);
            var indent = numbered.Groups[1].Value.Sum(character => character == '\t' ? 4 : 1);
            if (numbered.Success && indent <= itemIndent)
            {
                if (current is not null) items.Add(current.ToString());
                current = new StringBuilder();
                itemIndent = indent;
            }
            current?.AppendLine(line);
        }
        if (current is not null) items.Add(current.ToString());
        return items;
    }
}
