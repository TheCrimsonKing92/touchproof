using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// A scan exposes locations only; content and operator pattern values never leave this type.
internal sealed class AdoptedBuildLeakScanner
{
    internal const string GenericPatternPath = "config/adopted-build-leak-patterns.json";
    private readonly Regex[] _patterns;
    internal bool OwnerPatternsPresent { get; }
    internal static string OwnerPatternPath(string dataRoot) =>
        Path.Combine(dataRoot, "leak-scan", "owner-patterns.json");

    internal AdoptedBuildLeakScanner(string genericPath, string ownerPath)
    {
        var generic = Load(genericPath);
        OwnerPatternsPresent = File.Exists(ownerPath);
        _patterns = generic.Concat(OwnerPatternsPresent ? Load(ownerPath) : []).ToArray();
    }

    private static Regex[] Load(string path)
    {
        var values = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path));
        if (values is null || values.Count == 0 || values.Values.Any(string.IsNullOrWhiteSpace))
            throw new FormatException("Leak patterns must be a nonempty object of regular expressions.");
        return values.Values.Select(pattern => new Regex(pattern,
            RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1))).ToArray();
    }

    internal ScanResult Scan(string diff)
    {
        var hits = new List<Location>();
        string? path = null;
        var lineNumber = 0;
        var remaining = 0;
        var added = 0;
        using var reader = new StringReader(diff);
        while (reader.ReadLine() is { } line)
        {
            if (line.StartsWith("diff --git ", StringComparison.Ordinal)) { path = null; remaining = 0; }
            else if (remaining > 0 && line.StartsWith('+'))
            {
                if (path is null) throw new FormatException("Added line without a file header.");
                added++;
                if (_patterns.Any(pattern => pattern.IsMatch(line[1..])))
                    hits.Add(new(SafePath(path), lineNumber));
                lineNumber++;
                remaining--;
            }
            else if (remaining > 0 && line.StartsWith(' ')) { lineNumber++; remaining--; }
            else if (line.StartsWith("+++ ", StringComparison.Ordinal))
            {
                path = line[4..];
                if (path.StartsWith('"')) path = JsonSerializer.Deserialize<string>(path)!;
                if (path.StartsWith("b/", StringComparison.Ordinal)) path = path[2..];
            }
            else if (line.StartsWith("@@ ", StringComparison.Ordinal))
            {
                var hunk = Regex.Match(line, @"^@@ -\d+(?:,\d+)? \+(\d+)(?:,(\d+))? @@",
                    RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
                if (!hunk.Success) throw new FormatException("Invalid unified diff hunk.");
                lineNumber = int.Parse(hunk.Groups[1].Value, CultureInfo.InvariantCulture);
                remaining = hunk.Groups[2].Success
                    ? int.Parse(hunk.Groups[2].Value, CultureInfo.InvariantCulture) : 1;
            }
        }
        if (remaining != 0) throw new FormatException("Incomplete unified diff hunk.");
        return new(added, hits.Distinct().ToArray());
    }

    private string SafePath(string path)
    {
        foreach (var pattern in _patterns) path = pattern.Replace(path, "[redacted]");
        return path.Replace('\r', '_').Replace('\n', '_').Replace('\t', '_');
    }

    internal sealed record Location(string File, int Line);
    internal sealed record ScanResult(int AddedLines, IReadOnlyList<Location> Matches);
}
