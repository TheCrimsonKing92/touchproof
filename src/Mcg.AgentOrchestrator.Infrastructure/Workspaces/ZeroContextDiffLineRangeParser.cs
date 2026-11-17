using System.Globalization;
using System.Text.RegularExpressions;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class ZeroContextDiffLineRangeParser
{
    internal static IReadOnlyDictionary<string, IReadOnlyList<(int StartLine, int EndLine)>> Parse(string diffOutput)
    {
        var result = new Dictionary<string, IReadOnlyList<(int StartLine, int EndLine)>>(StringComparer.OrdinalIgnoreCase);
        string? path = null;
        var ranges = new List<(int StartLine, int EndLine)>();
        var inFile = false;
        var inHunk = false;
        var invalid = false;
        void FinishFile()
        {
            if (path is not null && !invalid && ranges.Count > 0) result[path] = ranges.ToArray();
        }
        using var reader = new StringReader(diffOutput);
        while (reader.ReadLine() is { } line)
        {
            if (line.StartsWith("diff --git ", StringComparison.Ordinal))
            {
                FinishFile();
                path = null;
                ranges.Clear();
                inFile = true;
                inHunk = invalid = false;
            }
            else if (inFile && !inHunk && line.StartsWith("+++ ", StringComparison.Ordinal))
            {
                var newPath = line[4..].TrimEnd('\t');
                if (path is not null || !newPath.StartsWith("b/", StringComparison.Ordinal)) invalid = true;
                else path = newPath[2..];
            }
            else if (inFile && line.StartsWith("@@", StringComparison.Ordinal))
            {
                inHunk = true;
                var match = Regex.Match(line, @"^@@ -\d+(?:,\d+)? \+(\d+)(?:,(\d+))? @@(?: .*)?$",
                    RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
                var count = 1;
                if (!match.Success || path is null ||
                    !int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var start) ||
                    (match.Groups[2].Success && !int.TryParse(match.Groups[2].Value,
                        NumberStyles.None, CultureInfo.InvariantCulture, out count)) ||
                    start < 1 || count < 1 || (long)start + count - 1 > int.MaxValue)
                {
                    // Deletions have no new-side span: even mixed files must retain whole-file selection.
                    invalid = true;
                }
                else ranges.Add((start, start + count - 1));
            }
        }
        FinishFile();
        return result;
    }
}
