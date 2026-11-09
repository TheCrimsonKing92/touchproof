using System.Text;
using System.Text.RegularExpressions;

namespace Mcg.AgentOrchestrator.Infrastructure;

/// <summary>Builds literal, segment-anchored patterns for Go's hierarchical -run matcher.</summary>
internal static class GoSubtestRunSelector
{
    private static readonly Regex SubtestNamePattern = new(
        @"\A(Test|Example|Fuzz)[A-Za-z0-9_]*(?:/[^\s,;/]+)+\z");

    internal static bool IsSubtestName(string name) => SubtestNamePattern.IsMatch(name);

    internal static string[] SplitNames(string names)
    {
        // Pipes delimit top-level unions only before the first slash. After it they are
        // literal subtest text; separate subtest selections use package:name; package:name.
        var slash = names.IndexOf('/');
        return slash < 0 ? names.Split('|')
            : names.Split('|', names[..slash].Count(character => character == '|') + 1);
    }

    internal static string BuildRunPattern(string name)
    {
        var pattern = new StringBuilder();
        foreach (var segment in name.Split('/'))
        {
            if (pattern.Length > 0)
                pattern.Append('/');
            pattern.Append('^');
            foreach (var character in segment)
            {
                // Go regexp.QuoteMeta's set; .NET Regex.Escape has different escaping rules.
                if (@"\.+*?()|[]{}^$".Contains(character))
                    pattern.Append('\\');
                pattern.Append(character);
            }
            pattern.Append('$');
        }
        return pattern.ToString();
    }
}
