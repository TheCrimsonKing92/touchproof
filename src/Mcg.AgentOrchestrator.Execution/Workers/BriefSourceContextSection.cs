using System.Globalization;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

/// <summary>Optional, independently bounded working-tree excerpts from a brief's measured premise.</summary>
internal static class BriefSourceContextSection
{
    private const int ContextLines = 3;
    private const int PerFileCap = 6000;
    private const int TotalCap = 20000;
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private static readonly Regex Heading = new(@"^ {0,3}(#{1,6})[ \t]+(.+?)\s*$");
    // Consume a whole path token, so an absolute/URI/drive path cannot yield a relative suffix.
    private static readonly Regex Citation = new(
        "(?<![^\\s`\"'()<>\\[\\]{},;])(?<path>[^\\s`\"'()<>\\[\\]{},;:]+):(?<start>[0-9]+)(?:-(?<end>[0-9]+))?(?![\\w:#-]|\\.[0-9])");

    internal static string Append(string prompt, string? briefText, string workingDirectory,
        string? candidateCommit, AgentRole role)
    {
        if (role is not (AgentRole.Planner or AgentRole.Reviewer))
            return prompt;
        var section = Render(briefText, workingDirectory, candidateCommit);
        return section is null ? prompt : prompt + "\n\n" + section;
    }

    internal static string? Render(string? briefText, string workingDirectory, string? candidateCommit)
    {
        if (string.IsNullOrWhiteSpace(briefText))
            return null;

        var files = new Dictionary<string, (string Path, List<(int Start, int End, int Order)> Ranges)>(PathComparer);
        var citationOrder = 0;
        try
        {
            var root = Path.GetFullPath(workingDirectory);
            var physicalRoot = ResolvePhysicalPath(root);
            foreach (var text in MeasuredPremiseLines(briefText))
            foreach (Match match in Citation.Matches(text))
            {
                if (!int.TryParse(match.Groups["start"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var start)
                    || start < 1)
                    continue;
                var end = start;
                if (match.Groups["end"].Success &&
                    (!int.TryParse(match.Groups["end"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out end)
                        || end < start))
                    continue;
                try
                {
                    var path = match.Groups["path"].Value;
                    if (Path.IsPathRooted(path) || path.Contains(':') || path.StartsWith('\\'))
                        continue;
                    path = path.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
                    var fullPath = Path.GetFullPath(path, root);
                    if (!IsWithin(root, fullPath))
                        continue;
                    var relative = Path.GetRelativePath(root, fullPath);
                    var resolved = ResolvePhysicalPath(Path.Combine(physicalRoot, relative), physicalRoot);
                    if (!File.Exists(resolved) || Directory.Exists(resolved))
                        continue;
                    if (!files.TryGetValue(resolved, out var file))
                    {
                        file = (relative.Replace('\\', '/'), []);
                        files.Add(resolved, file);
                    }
                    file.Ranges.Add((start, end, citationOrder++));
                }
                catch (Exception error) when (IsReadFailure(error)) { }
            }
        }
        catch (Exception error) when (IsReadFailure(error))
        {
            return null;
        }

        var blocks = new List<(string Path, string Block, int Order)>();
        foreach (var (path, file) in files)
        {
            try
            {
                var block = ReadBlock(path, file.Path, file.Ranges);
                if (block is { } readable)
                    blocks.Add((file.Path, readable.Block, readable.Order));
            }
            catch (Exception error) when (IsReadFailure(error)) { }
        }
        if (blocks.Count == 0)
            return null;
        blocks.Sort((left, right) => left.Order.CompareTo(right.Order));

        var candidate = string.IsNullOrWhiteSpace(candidateCommit) ? "(head unavailable)" : candidateCommit;
        var section = new StringBuilder("## Source context\n");
        section.Append($"These working-tree lines are current at candidate commit {candidate}. These excerpts are the current text of those line ranges. Use them instead of reading those ranges again; read beyond them with an offset and limit only when a decision needs lines the excerpt does not show.\n");
        // A head identifier is normally a SHA; still keep the complete section bounded for any caller.
        if (section.Length > TotalCap - PerFileCap)
            return null;
        var admitted = 0;
        foreach (var file in blocks)
        {
            var remaining = blocks.Count - admitted - 1;
            var reserve = remaining > 0 ? OverflowHeading(blocks.Count).Length + OverflowSummary(blocks.Count).Length : 0;
            if (section.Length + file.Block.Length + reserve > TotalCap)
                break;
            section.Append(file.Block);
            admitted++;
        }
        if (admitted < blocks.Count)
        {
            section.Append(OverflowHeading(blocks.Count - admitted));
            var listed = admitted;
            while (listed < blocks.Count)
            {
                var entry = $"- {blocks[listed].Path}\n";
                var reserve = listed + 1 < blocks.Count ? OverflowSummary(blocks.Count - listed - 1).Length : 0;
                if (section.Length + entry.Length + reserve > TotalCap)
                    break;
                section.Append(entry);
                listed++;
            }
            if (listed < blocks.Count)
                section.Append(OverflowSummary(blocks.Count - listed));
        }
        return section.ToString();
    }

    private static string OverflowHeading(int count) => $"\nTotal-cap truncation: {count} files omitted from excerpts (paths only):\n";
    private static string OverflowSummary(int count) => $"Further omitted files: {count}.\n";

    private static IEnumerable<string> MeasuredPremiseLines(string brief)
    {
        using var reader = new StringReader(brief);
        var premiseLevel = 0;
        char fenceCharacter = '\0';
        var fenceLength = 0;
        while (reader.ReadLine() is { } line)
        {
            var trimmed = line.TrimStart();
            var run = trimmed.Length > 0 ? trimmed.TakeWhile(c => c == trimmed[0]).Count() : 0;
            if (fenceCharacter != '\0')
            {
                if (trimmed.Length > 0 && trimmed[0] == fenceCharacter && run >= fenceLength
                    && string.IsNullOrWhiteSpace(trimmed[run..]))
                    fenceCharacter = '\0';
                else if (premiseLevel > 0)
                    yield return line;
                continue;
            }
            if (run >= 3 && trimmed[0] is '`' or '~')
            {
                fenceCharacter = trimmed[0];
                fenceLength = run;
                continue;
            }
            var heading = Heading.Match(line);
            if (heading.Success)
            {
                var level = heading.Groups[1].Length;
                var title = Regex.Replace(heading.Groups[2].Value, @"\s+#+\s*$", "").Trim();
                if (level <= premiseLevel)
                    premiseLevel = 0;
                if (title.Equals("Measured premise", StringComparison.OrdinalIgnoreCase))
                    premiseLevel = premiseLevel == 0 ? level : Math.Min(premiseLevel, level);
                continue;
            }
            if (premiseLevel > 0)
                yield return line;
        }
    }

    private static string ResolvePhysicalPath(string path, string? boundary = null)
    {
        var root = Path.GetPathRoot(path)!;
        var current = root;
        foreach (var part in path[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (info.LinkTarget is not null)
                current = info.ResolveLinkTarget(returnFinalTarget: true)?.FullName
                    ?? throw new IOException("Unresolvable source link.");
            // Ancestors of the boundary are traversed while resolving the absolute path.
            if (boundary is not null && !PathComparer.Equals(current, boundary)
                && !IsWithin(current, boundary) && !IsWithin(boundary, current))
                throw new IOException("Source path leaves the working directory.");
        }
        if (boundary is not null && !IsWithin(boundary, current))
            throw new IOException("Source path leaves the working directory.");
        return current;
    }

    private static bool IsWithin(string root, string path) =>
        path.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static bool IsReadFailure(Exception error) => error is IOException or UnauthorizedAccessException
        or ArgumentException or NotSupportedException or SecurityException;

    private static (string Block, int Order)? ReadBlock(string fullPath, string displayPath,
        List<(int Start, int End, int Order)> citations)
    {
        var expanded = citations.OrderBy(range => range.Start)
            .Select(range => (Start: Math.Max(1, range.Start - ContextLines),
                End: (int)Math.Min(int.MaxValue, (long)range.End + ContextLines), CitedStart: range.Start))
            .ToList();
        var ranges = new List<(int Start, int End, int CitedStart)>();
        foreach (var range in expanded)
        {
            if (ranges.Count > 0 && (long)range.Start <= (long)ranges[^1].End + 1)
                ranges[^1] = (ranges[^1].Start, Math.Max(ranges[^1].End, range.End), ranges[^1].CitedStart);
            else
                ranges.Add(range);
        }

        // Retain only the prefix that can contribute to the rendered block, not the entire file.
        var source = new List<(int Number, string Text)>();
        var rangeIndex = 0;
        var number = 0;
        var retained = 0;
        foreach (var line in File.ReadLines(fullPath))
        {
            number++;
            while (rangeIndex < ranges.Count && number > ranges[rangeIndex].End)
                rangeIndex++;
            if (rangeIndex == ranges.Count)
                break;
            if (number < ranges[rangeIndex].Start)
                continue;
            if (retained <= PerFileCap)
            {
                source.Add((number, line.Length > PerFileCap ? line[..PerFileCap] + " [partial line]" : line));
                retained += source[^1].Text.Length + 8;
            }
        }
        ranges = ranges.Where(range => range.CitedStart <= number).ToList();
        source = source.Where(line => ranges.Any(range => line.Number >= range.Start && line.Number <= range.End)).ToList();
        if (source.Count == 0)
            return null;

        // Numbered lines cannot close a Markdown fence. Bound its length as well, so an
        // oversized delimiter in the source cannot consume the entire excerpt budget.
        var fence = new string('`', Math.Clamp(source.Max(line => LongestBacktickRun(line.Text)) + 1, 3, 32));
        // Prefixing source with its line number also prevents source headings becoming prompt sections.
        var body = new StringBuilder();
        var shown = new List<(int Start, int End)>();
        var truncated = false;
        foreach (var (lineNumber, text) in source)
        {
            var gap = shown.Count > 0 && lineNumber > shown[^1].End + 1
                ? $"... omitted lines {shown[^1].End + 1}-{lineNumber - 1} ...\n" : "";
            var nextShown = new List<(int Start, int End)>(shown);
            if (nextShown.Count > 0 && lineNumber == nextShown[^1].End + 1)
                nextShown[^1] = (nextShown[^1].Start, lineNumber);
            else
                nextShown.Add((lineNumber, lineNumber));
            var label = $"{lineNumber} | ";
            var header = FileHeading(displayPath, nextShown);
            const string marker = "[source truncated]\n";
            var available = PerFileCap - header.Length - fence.Length * 2 - 2 - body.Length - gap.Length - marker.Length;
            if (label.Length + text.Length + 1 > available)
            {
                // Keep complete lines where possible; an individually oversized first line still contributes.
                const string partialMarker = " [partial line]\n";
                var take = available - label.Length - partialMarker.Length;
                if (body.Length == 0 && take > 0)
                {
                    body.Append(gap).Append(label).Append(text.AsSpan(0, Math.Min(take, text.Length))).Append(partialMarker);
                    shown = nextShown;
                }
                truncated = true;
                break;
            }
            body.Append(gap).Append(label).Append(text).Append('\n');
            shown = nextShown;
        }
        if (shown.Count == 0)
            return null;
        truncated |= source[^1].Number < ranges[^1].End && source[^1].Number < number
            || retained > PerFileCap;
        return (FileHeading(displayPath, shown) + fence + "\n" + body
            + (truncated ? "[source truncated]\n" : "") + fence + "\n",
            citations.Where(citation => citation.Start <= number).Min(citation => citation.Order));
    }

    private static string FileHeading(string path, List<(int Start, int End)> ranges) =>
        $"\n### {path} (lines {string.Join(", ", ranges.Select(range => $"{range.Start}-{range.End}"))})\n";

    private static int LongestBacktickRun(string text)
    {
        var longest = 0;
        var current = 0;
        foreach (var character in text)
        {
            current = character == '`' ? current + 1 : 0;
            longest = Math.Max(longest, current);
        }
        return longest;
    }
}
