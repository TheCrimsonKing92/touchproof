using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

// Mirrors the explicit Scope region of GoalFileScopeInference. The parity test is
// the contract: neither inferred references nor text outside that region is scope.
internal static class DocumentationScopePaths
{
    private static readonly Regex KnownPath = new(
        @"(?<![\w.-])(?:src|tests|scripts|docs|config|\.agents|\.github)[\\/][A-Za-z0-9_.*?\[\]{}\\/\-]+",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex Exclusion = new(
        @"(?:(?:do\s+not|don't|must\s+not|never)\s+(?:touch|change|modify|edit|alter|rename|delete))\s+(?<targets>.*?)(?=;|\.(?:\s|$)|\r?$|\n)",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    internal static IReadOnlyList<string> FromText(string text)
    {
        var lines = text.ReplaceLineEndings("\n").Split('\n');
        var start = Array.FindIndex(lines, line => line.StartsWith('#') &&
            line.TrimStart('#').Trim().Equals("Scope", StringComparison.OrdinalIgnoreCase));
        if (start < 0) return [];
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(start + 1))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith('#') || trimmed.StartsWith("Out of scope", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("Post-landing check", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("Dependencies:", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("Verification ownership", StringComparison.OrdinalIgnoreCase) ||
                trimmed == "Target files/scopes:") break;
            var token = trimmed.StartsWith("- ", StringComparison.Ordinal) ? trimmed[2..].Trim() : trimmed;
            var unquoted = token.IndexOfAny(['/', '\\']) >= 0 && !token.Contains('`') &&
                !token.Any(char.IsWhiteSpace) && !(token.Length >= 2 && token[0] is '\'' or '"' && token[^1] == token[0])
                ? token : null;
            if (unquoted is not null && !Safe(unquoted.Replace('\\', '/'))) continue;
            foreach (Match match in KnownPath.Matches(line))
            {
                if (unquoted is not null && match.Index != line.IndexOf(unquoted, StringComparison.Ordinal)) continue;
                if ((match.Index > 0 && line[match.Index - 1] is '/' or '\\') ||
                    line.AsSpan(match.Index + match.Length).StartsWith("::", StringComparison.Ordinal) ||
                    match.Value.IndexOfAny(['*', '?']) >= 0 || NumericSegment(match.Value)) continue;
                if (Exclusion.Matches(line).Any(exclusion => match.Index >= exclusion.Index &&
                        match.Index < exclusion.Index + exclusion.Length)) continue;
                paths.Add(Normalize(match.Value));
            }
            foreach (Match match in Regex.Matches(line, @"`([^`]*)`"))
            {
                var path = match.Groups[1].Value.TrimEnd('.', ',', ';', ':', ')');
                if (Safe(path) && HasShape(path)) paths.Add(Normalize(path));
            }
            if (unquoted is not null && Safe(unquoted) && HasShape(unquoted) &&
                unquoted.IndexOfAny(['#', ',', ';', '\'', '"']) < 0 && !unquoted.EndsWith(')') &&
                unquoted.TrimEnd('/').Split('/').All(segment => segment.Length > 0 && segment != "...") &&
                !unquoted.EndsWith("//", StringComparison.Ordinal)) paths.Add(Normalize(unquoted));
        }
        return paths.Where(candidate => !paths.Any(other => other.Length > candidate.Length &&
                RepositoryPathOverlap.Classify(candidate, other) == PathOverlapKind.DirectoryPrefix))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    internal static bool Covers(string entry, string path) =>
        path.Equals(entry, StringComparison.Ordinal) ||
        (entry.EndsWith('/') && path.StartsWith(entry, StringComparison.Ordinal));

    private static string Normalize(string path) => path.Replace('\\', '/').Trim().TrimEnd('.', ',', ';', ':', ')', ']');
    private static bool NumericSegment(string path) => path.Split('/', '\\').Any(segment =>
        segment.Length > 0 && segment.All(character => character is >= '0' and <= '9'));
    private static bool Safe(string path) => path.Contains('/') && !path.StartsWith('/') &&
        !path.Any(char.IsWhiteSpace) && path.IndexOfAny([':', '\\', '*', '?', '[', ']', '{', '}', '~', '|', '=', '<', '>']) < 0 &&
        !path.Split('/').Any(segment => segment is "." or "..") && !NumericSegment(path);
    private static bool HasShape(string path)
    {
        var name = path[(path.LastIndexOf('/') + 1)..];
        var dot = name.LastIndexOf('.');
        return path.EndsWith('/') || (dot > 0 && dot < name.Length - 1);
    }
}
