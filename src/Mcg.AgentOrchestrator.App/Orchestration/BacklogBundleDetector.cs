using System.Text.RegularExpressions;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class BacklogBundleDetector
{
    private static readonly Regex Heading = new(@"^#{1,6}(?:[ \t]+(.*)|$)", RegexOptions.CultureInvariant);
    private static readonly Regex SetextUnderline = new(@"^(?:=+|-+)$", RegexOptions.CultureInvariant);
    private static readonly Regex SectionLabel = new(@"^\p{L}[\p{L}\p{N} \t_-]*:$", RegexOptions.CultureInvariant);
    private static readonly Regex PartMarker = new(
        @"^([ \t]*)(?:\(0*[1-9][0-9]*\)|0*[1-9][0-9]*[.)])(?:[ \t]+|$)",
        RegexOptions.CultureInvariant);

    internal static IReadOnlyList<string> FindParts(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return [];

        var lines = ReadWantedLines(body).ToArray();
        var entries = new List<(int Line, int Indent, int ContentStart)>();
        for (var index = 0; index < lines.Length; index++)
        {
            var marker = PartMarker.Match(lines[index]);
            if (marker.Success)
                entries.Add((index, IndentWidth(marker.Groups[1].Value), marker.Length));
        }
        if (entries.Count < 2)
            return [];

        var minIndent = entries.Min(entry => entry.Indent);
        var topLevel = entries.Where(entry => entry.Indent == minIndent).ToArray();
        var parts = new List<string>();
        for (var index = 0; index < topLevel.Length; index++)
        {
            var entry = topLevel[index];
            var end = index + 1 < topLevel.Length ? topLevel[index + 1].Line : lines.Length;
            var content = lines[entry.Line..end];
            content[0] = content[0][entry.ContentStart..];
            var part = string.Join('\n', content).Trim();
            if (part.Length > 0)
                parts.Add(part);
        }
        return parts.Count >= 2 ? parts : [];
    }

    internal static string RenderParts(IReadOnlyList<string> parts) =>
        string.Join(" | ", parts.Select((part, index) =>
            $"{index + 1}. {string.Join(' ', part.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))}"));

    private static IEnumerable<string> ReadWantedLines(string body)
    {
        var lines = body.ReplaceLineEndings("\n").Split('\n');
        var inWanted = false;
        char fence = '\0';
        var fenceLength = 0;
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            var text = line.Trim();
            var markerLength = text.Length > 0 && text[0] is '`' or '~'
                ? text.TakeWhile(character => character == text[0]).Count() : 0;
            if (fenceLength > 0)
            {
                if (markerLength >= fenceLength && text[0] == fence && markerLength == text.Length)
                    fenceLength = 0;
                continue;
            }
            if (markerLength >= 3)
            {
                fence = text[0];
                fenceLength = markerLength;
                continue;
            }

            const string wantedLabel = "Wanted:";
            if (text.StartsWith(wantedLabel, StringComparison.OrdinalIgnoreCase))
            {
                if (inWanted)
                    yield break;
                inWanted = true;
                var inlineContent = text[wantedLabel.Length..].TrimStart();
                if (inlineContent.Length > 0)
                    yield return inlineContent;
                continue;
            }

            var heading = Heading.Match(text);
            var isSetext = !heading.Success && text.Length > 0 &&
                !PartMarker.IsMatch(line) &&
                index + 1 < lines.Length && SetextUnderline.IsMatch(lines[index + 1].Trim());
            if (heading.Success || isSetext)
            {
                if (inWanted)
                    yield break;
                var title = isSetext ? text : Regex.Replace(heading.Groups[1].Value, @"[ \t]+#+$", "").Trim();
                inWanted = title.Equals("Wanted", StringComparison.OrdinalIgnoreCase);
                if (isSetext)
                    index++;
                continue;
            }
            if (inWanted)
            {
                if (SectionLabel.IsMatch(text))
                    yield break;
                yield return line;
            }
        }
    }

    private static int IndentWidth(string indent)
    {
        var width = 0;
        foreach (var character in indent)
            width += character == '\t' ? 4 - width % 4 : 1;
        return width;
    }
}
