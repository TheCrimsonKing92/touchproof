using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class RepositoryScopeLabels
{
    private static readonly Regex KeyedLabel = new(
        @"^In scope,\s+.+\(key\s+(?<key>.*?)\):\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    // A null entry rejects the whole block, so its paths cannot inherit a previous key.
    public static string?[] Map(
        IReadOnlyList<string> lines,
        GoalRepositorySet? repositories,
        ICollection<string>? warnings = null)
    {
        var keys = Enumerable.Repeat<string?>("default", lines.Count).ToArray();
        if (repositories is null || BriefScopeSectionSelector.FindScopeRegion(lines) is not { } region)
            return keys;

        string? current = "default";
        for (var index = region.Start; index < region.End; index++)
        {
            var line = lines[index].Trim();
            var label = KeyedLabel.Match(line);
            if (label.Success)
            {
                var key = label.Groups["key"].Value.Trim();
                if (key.Length >= 2 && key[0] == '`' && key[^1] == '`')
                    key = key[1..^1];
                current = repositories.Contains(key)
                    ? repositories.Keys.Single(candidate => candidate.Equals(key, StringComparison.OrdinalIgnoreCase))
                    : null;
                if (current is null)
                    warnings?.Add($"Scope label names undeclared repository key '{key}'; its paths were ignored.");
                keys[index] = null;
                continue;
            }

            if (line.Equals("In scope:", StringComparison.OrdinalIgnoreCase))
                current = "default";
            keys[index] = current;
        }

        return keys;
    }
}
