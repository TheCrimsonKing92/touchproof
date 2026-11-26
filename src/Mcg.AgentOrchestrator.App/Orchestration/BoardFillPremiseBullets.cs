using System.Text;
using System.Text.RegularExpressions;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class BoardFillPremiseBullets
{
    private const string RuntimeObservationPrefix = "runtime-observation:";
    private static readonly Regex Marker = new(@"^[ \t]*[-*][ \t]+\S", RegexOptions.Multiline);
    private static readonly Regex CitationBoundary = new(
        @"^[ \t]*(?=\n(?<continuationIndent>[ \t]*)\S)" +
        @"|^[ \t]*(?:>|(?:\+|[0-9]{1,9}[.)])[ \t]+(?=\S))" +
        @"|^(?<continuationIndent>[ \t]*)(?:-(?:[ \t]*-){2,}|\*(?:[ \t]*\*){2,}|_(?:[ \t]*_){2,})[ \t]*$",
        RegexOptions.Multiline);
    private static readonly Regex EvidencePath = new("""(?:^|\s)\S*[/\\][^\s/\\]*\.[\p{L}\p{N}]+(?=[\x60'"\])>.,;:!?]*(?:\s|$))""");
    private static readonly Regex NoteCitation = new("""\bnote\s+[0-9a-fA-F]+(?=[*\x60'"\p{Pf}\])>.,;:!?]*(?:\s|$))""");

    internal static string MeasuredPremise(string markdown)
    {
        var text = markdown.ReplaceLineEndings("\n");
        var headings = Regex.Matches(text, @"^##[ \t]+([^\n]+)", RegexOptions.Multiline);
        var start = headings.FirstOrDefault(heading => heading.Groups[1].Value.Trim()
            .Equals("Measured premise", StringComparison.OrdinalIgnoreCase));
        if (start is null) return "";
        var end = headings.FirstOrDefault(heading => heading.Index > start.Index)?.Index ?? text.Length;
        return text[start.Index..end].TrimEnd();
    }

    internal static IReadOnlyList<Bullet> SourceClaims(string premise) =>
        Find(premise.ReplaceLineEndings("\n")).Where(bullet => !bullet.IsRuntimeObservation).ToArray();

    internal static string SourceClaimPremise(string premise)
    {
        var text = premise.ReplaceLineEndings("\n");
        StringBuilder? filtered = null;
        var start = 0;
        foreach (var bullet in Find(text))
        {
            if (!bullet.IsRuntimeObservation) continue;
            filtered ??= new StringBuilder(text.Length);
            filtered.Append(text.AsSpan(start, bullet.Start - start));
            start = bullet.End;
        }
        return filtered is null ? premise : filtered.Append(text.AsSpan(start)).ToString().TrimEnd();
    }

    private static IEnumerable<Bullet> Find(string text)
    {
        var markers = Marker.Matches(text);
        for (var index = 0; index < markers.Count; index++)
        {
            var marker = markers[index];
            var end = index + 1 < markers.Count ? markers[index + 1].Index : text.Length;
            var bodyStart = marker.Index + marker.Length - 1;
            var body = text[bodyStart..end];

            if (body.StartsWith(RuntimeObservationPrefix, StringComparison.Ordinal))
            {
                var bodyEnd = CitationEnd(body, marker.ValueSpan[..^1]);
                var evidence = body[RuntimeObservationPrefix.Length..bodyEnd];
                if (EvidencePath.IsMatch(evidence) || NoteCitation.IsMatch(evidence))
                {
                    var observationEnd = bodyStart + bodyEnd;
                    yield return new(marker.Index, observationEnd, text[marker.Index..observationEnd].TrimEnd(), true);
                    continue;
                }
            }

            // Revision feedback retains the existing source-claim span, including continuation lines.
            yield return new(marker.Index, end, text[marker.Index..end].TrimEnd(), false);
        }
    }

    private static int CitationEnd(string body, ReadOnlySpan<char> markerPrefix)
    {
        var contentColumn = MarkdownColumnWidth(markerPrefix);
        foreach (Match boundary in CitationBoundary.Matches(body))
        {
            // Paragraphs after blank lines and thematic breaks belong to the item at its content indentation.
            var indent = boundary.Groups["continuationIndent"];
            if (!indent.Success || MarkdownColumnWidth(indent.ValueSpan) < contentColumn)
                return boundary.Index;
        }

        return body.Length;
    }

    private static int MarkdownColumnWidth(ReadOnlySpan<char> prefix)
    {
        const int tabWidth = 4;
        var column = 0;
        foreach (var character in prefix)
            column += character == '\t' ? tabWidth - column % tabWidth : 1;
        return column;
    }

    internal sealed record Bullet(int Start, int End, string Text, bool IsRuntimeObservation);
}
