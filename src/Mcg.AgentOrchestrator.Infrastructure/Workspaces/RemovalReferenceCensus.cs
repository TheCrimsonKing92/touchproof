using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Mcg.AgentOrchestrator.Infrastructure;

public enum RemovalReferenceClassification { MustChange, Leave }

public sealed record RemovalReferenceHit(string Name, string Path, int Line, string Text,
    RemovalReferenceClassification Classification, string Rule);

public sealed record RemovalReferenceCensusResult(IReadOnlyList<string> Names, IReadOnlyList<RemovalReferenceHit> Hits);

/// <summary>A filing-time census of literal names in the repository's tracked working tree.</summary>
public static class RemovalReferenceCensus
{
    public const string NoReferences = "no references in src, tests, config, README.md or docs.";
    private static readonly string[] ScanRoots = ["src", "tests", "config", "README.md", "docs"];

    public static RemovalReferenceCensusResult Run(IReadOnlyList<string> removedNames, string repositoryRoot)
    {
        ArgumentNullException.ThrowIfNull(removedNames);
        var names = removedNames.Select(name => name?.Trim() ?? "").Distinct(StringComparer.Ordinal).ToArray();
        if (names.Any(name => name.Length == 0 || name.Any(char.IsControl)))
            throw new ArgumentException("Removed names must be nonempty single-line literals.", nameof(removedNames));

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repositoryRoot));
        var top = GitCli.RunForRepository(root, "rev-parse", "--show-toplevel");
        RequireSuccess(top, "rev-parse");
        if (!Path.TrimEndingDirectorySeparator(Path.GetFullPath(top.Output.Trim())).Equals(root,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidOperationException("Removal reference census: not a repository root.");

        var roots = ScanRoots.Where(path => Directory.Exists(Path.Combine(root, path)) ||
            File.Exists(Path.Combine(root, path))).ToArray();
        var hits = new List<RemovalReferenceHit>();
        foreach (var name in names)
        {
            if (roots.Length == 0) break;
            var grep = GitCli.RunForRepository(root,
                ["grep", "--no-color", "--no-column", "--no-heading", "--no-break", "-n", "-z", "-I", "-F", "-e", name, "--", .. roots]);
            if (grep.ProcessStarted && !grep.DrainTimedOut && grep.ExitCode == 1 &&
                string.IsNullOrWhiteSpace(grep.Error) && grep.Output.Length == 0)
                continue;
            RequireSuccess(grep, "grep");
            var nameHits = new List<RemovalReferenceHit>();
            for (var offset = 0; offset < grep.Output.Length;)
            {
                var pathEnd = grep.Output.IndexOf('\0', offset);
                var lineEnd = pathEnd < 0 ? -1 : grep.Output.IndexOf('\0', pathEnd + 1);
                if (lineEnd < 0 || !int.TryParse(grep.Output.AsSpan(pathEnd + 1, lineEnd - pathEnd - 1),
                        NumberStyles.None, CultureInfo.InvariantCulture, out var line) || line < 1)
                    throw new InvalidOperationException("Removal reference census: malformed git grep output.");
                var textEnd = grep.Output.IndexOf('\n', lineEnd + 1);
                if (textEnd < 0) textEnd = grep.Output.Length;
                var path = grep.Output[offset..pathEnd].Replace('\\', '/');
                var text = grep.Output[(lineEnd + 1)..textEnd].TrimEnd('\r');
                var (classification, rule) = Classify(path, text);
                nameHits.Add(new(name, path, line, text, classification, rule));
                offset = textEnd + 1;
            }
            hits.AddRange(nameHits.OrderBy(hit => hit.Path, StringComparer.Ordinal).ThenBy(hit => hit.Line));
        }
        return new(names, hits);
    }

    /// <summary>
    /// One ordered rule site: full comment lines (language-specific prefixes), docs/triage,
    /// docs/negative-controls, any fixtures directory, then docs with a YYYY-MM[-DD] filename
    /// token are leave. All other scanned references are must-change. Inline comments mixed
    /// with code remain must-change; a Markdown heading is not a shell comment.
    /// </summary>
    private static (RemovalReferenceClassification Classification, string Rule) Classify(string path, string text)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        var trimmed = text.TrimStart();
        var comment = extension switch
        {
            ".cs" or ".js" or ".ts" or ".jsx" or ".tsx" or ".c" or ".cpp" or ".h" or ".java" =>
                trimmed.StartsWith("//", StringComparison.Ordinal) || trimmed.StartsWith("/*", StringComparison.Ordinal) ||
                trimmed.StartsWith('*'),
            ".ps1" or ".psm1" or ".sh" or ".yml" or ".yaml" or ".toml" => trimmed.StartsWith('#'),
            ".md" or ".xml" or ".csproj" or ".props" or ".targets" => trimmed.StartsWith("<!--", StringComparison.Ordinal),
            _ => false
        };
        var rule = comment ? "comment"
            : path.StartsWith("docs/triage/", StringComparison.OrdinalIgnoreCase) ? "triage-doc"
            : path.StartsWith("docs/negative-controls/", StringComparison.OrdinalIgnoreCase) ? "negative-control-doc"
            : path.Split('/').SkipLast(1).Contains("fixtures", StringComparer.OrdinalIgnoreCase) ? "fixture"
            : path.StartsWith("docs/", StringComparison.OrdinalIgnoreCase) &&
                Regex.IsMatch(Path.GetFileName(path), @"(?<!\d)\d{4}-(?:0[1-9]|1[0-2])(?:-(?:0[1-9]|[12]\d|3[01]))?(?!\d)")
                ? "historical-doc" : "tracked-reference";
        return (rule == "tracked-reference" ? RemovalReferenceClassification.MustChange : RemovalReferenceClassification.Leave, rule);
    }

    public static string RenderBriefList(RemovalReferenceCensusResult result)
    {
        var output = new StringBuilder().AppendLine($"Removed names: {string.Join(", ", result.Names)}");
        foreach (var group in result.Hits.Where(hit => hit.Classification == RemovalReferenceClassification.MustChange)
                     .GroupBy(hit => hit.Path).OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            output.AppendLine($"- `{group.Key}`");
            foreach (var hit in group.OrderBy(hit => hit.Line).ThenBy(hit => hit.Name, StringComparer.Ordinal))
                output.AppendLine($"  - line {hit.Line} ({hit.Name}): {hit.Text}");
        }
        foreach (var name in result.Names.Where(name => !result.Hits.Any(hit => hit.Name == name)))
            output.AppendLine($"- {name}: {NoReferences}");
        output.Append($"Leave hits: {result.Hits.Count(hit => hit.Classification == RemovalReferenceClassification.Leave)}");
        return output.ToString();
    }

    public static bool TryParseRemovedNames(string brief, out IReadOnlyList<string> names)
    {
        var inBuildSection = false;
        var inFence = false;
        foreach (var line in brief.ReplaceLineEndings("\n").Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("```", StringComparison.Ordinal) || trimmed.StartsWith("~~~", StringComparison.Ordinal))
            {
                inFence = !inFence;
                continue;
            }
            if (inFence) continue;
            var heading = Regex.Match(trimmed, @"^(#{1,2})\s+(.+?)\s*#*$");
            if (heading.Success)
                inBuildSection = heading.Groups[1].Value == "##" &&
                    heading.Groups[2].Value.Equals("What to build", StringComparison.OrdinalIgnoreCase);
            if (trimmed.StartsWith("- ", StringComparison.Ordinal)) trimmed = trimmed[2..].TrimStart();
            if (!inBuildSection || !trimmed.StartsWith("Removed names:", StringComparison.OrdinalIgnoreCase)) continue;
            names = trimmed["Removed names:".Length..].Split(',').Select(name => name.Trim().Trim('`').Trim())
                .Where(name => name.Length > 0).Distinct(StringComparer.Ordinal).ToArray();
            return names.Count > 0;
        }
        names = [];
        return false;
    }

    private static void RequireSuccess(GitCli.GitResult result, string operation)
    {
        if (!result.ProcessStarted || result.DrainTimedOut || result.ExitCode != 0)
            throw new InvalidOperationException($"Removal reference census git {operation} failed " +
                $"(exit={result.ExitCode}, started={result.ProcessStarted}, drainTimedOut={result.DrainTimedOut}): {result.Error.Trim()}");
    }
}
