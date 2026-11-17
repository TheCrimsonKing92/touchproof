using System.Text;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class WorkerGuidanceScope
{
    internal static string Project(string guidanceText, AgentRole role)
    {
        var sections = new List<(int Start, string Title)>();
        var commentStart = -1;
        var fenceCharacter = '\0';
        var fenceLength = 0;

        for (var start = 0; start < guidanceText.Length;)
        {
            var end = start;
            while (end < guidanceText.Length && guidanceText[end] is not ('\r' or '\n'))
                end++;
            var line = guidanceText.AsSpan(start, end - start);
            var markdown = line;
            var indent = 0;
            while (indent < markdown.Length && markdown[indent] == ' ')
                indent++;
            markdown = indent <= 3 ? markdown[indent..] : ReadOnlySpan<char>.Empty;

            if (TryFence(markdown, out var character, out var length))
            {
                if (fenceCharacter == '\0' &&
                    (character != '`' || !markdown[length..].Contains('`')))
                {
                    fenceCharacter = character;
                    fenceLength = length;
                }
                else if (character == fenceCharacter && length >= fenceLength &&
                         markdown[length..].IsWhiteSpace())
                {
                    fenceCharacter = '\0';
                }
                commentStart = -1;
            }
            else if (fenceCharacter == '\0')
            {
                if (TryHeading(markdown, out var title))
                {
                    // Section anchors belong to the following heading, not the prior body.
                    sections.Add((commentStart >= 0 ? commentStart : start, title.ToString()));
                    commentStart = -1;
                }
                else if (line.Trim().StartsWith("<!--", StringComparison.Ordinal) &&
                         line.Trim().EndsWith("-->", StringComparison.Ordinal))
                {
                    if (commentStart < 0)
                        commentStart = start;
                }
                else
                {
                    commentStart = -1;
                }
            }

            start = end;
            if (start < guidanceText.Length && guidanceText[start] == '\r')
                start++;
            if (start < guidanceText.Length && guidanceText[start] == '\n')
                start++;
        }

        var result = new StringBuilder(guidanceText.Length);
        var retainedStart = 0;
        for (var index = 0; index < sections.Count; index++)
        {
            var (start, title) = sections[index];
            if (!Excluded(title, role))
                continue;

            result.Append(guidanceText, retainedStart, start - retainedStart);
            retainedStart = index + 1 < sections.Count ? sections[index + 1].Start : guidanceText.Length;
        }
        if (retainedStart == 0)
            return guidanceText;

        result.Append(guidanceText, retainedStart, guidanceText.Length - retainedStart);
        return result.ToString();
    }

    private static bool Excluded(string title, AgentRole role) =>
        title is "Dogfood Boundary" or "Operating the goal loop" ||
        (role is AgentRole.Researcher or AgentRole.Tester && title == "Architecture & Design Discipline");

    private static bool TryHeading(ReadOnlySpan<char> line, out ReadOnlySpan<char> title)
    {
        title = default;
        if (!line.StartsWith("##", StringComparison.Ordinal) ||
            (line.Length > 2 && line[2] is not (' ' or '\t')))
            return false;

        title = line[2..].Trim();
        var closingStart = title.Length;
        while (closingStart > 0 && title[closingStart - 1] == '#')
            closingStart--;
        if (closingStart == 0 ||
            (closingStart < title.Length && title[closingStart - 1] is ' ' or '\t'))
            title = title[..closingStart].TrimEnd();
        return true;
    }

    private static bool TryFence(ReadOnlySpan<char> line, out char character, out int length)
    {
        character = line.IsEmpty ? '\0' : line[0];
        length = 0;
        if (character is not ('`' or '~'))
            return false;
        while (length < line.Length && line[length] == character)
            length++;
        return length >= 3;
    }
}
