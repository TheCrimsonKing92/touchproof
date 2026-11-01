using System.Text.RegularExpressions;

namespace Mcg.AgentOrchestrator.Core;

internal static class OutsideRootWriteRefusalClassification
{
    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(100);
    private static readonly Regex Refusal = new(
        "\\b(?:EPERM|EACCES)\\s*:\\s*(?:operation not permitted|permission denied|access (?:is )?denied)\\s*,?\\s*" +
        "(?<operation>open|mkdir|write|unlink|rmdir|rename|copyfile|symlink|link|chmod|utimes?)\\s+" +
        "(?<quote>['\"])(?<path>[^'\"\\r\\n]+)\\k<quote>" +
        "(?:\\s*,?\\s*(?:flags?|mode)\\s*[=:]\\s*['\"]?(?<flags>[rwa+bx]+)['\"]?)?", Options, MatchTimeout);
    private static readonly Regex DotnetRefusal = new(
        "\\b(?:write|writing|create|creating|delete|deleting)\\s*:?\\s*Access to the path\\s+" +
        "(?<quote>['\"])(?<path>[^'\"\\r\\n]+)\\k<quote>\\s+is denied\\.?", Options, MatchTimeout);
    private static readonly Regex OtherFault = new(
        @"Assert\.|\bexpected\b|Received\s*:|\berror\s*[-:]\s*\w|\b(?:\w+Error|\w+Exception|ENOENT|ENOTFOUND|ETIMEDOUT)\b",
        Options, MatchTimeout);
    private static readonly Regex Permission = new(@"\b(?:EPERM|EACCES|access (?:is )?denied)\b|Access to the path", Options, MatchTimeout);
    // After consuming all refusal diagnostics, accept only status/count/cause connective words.
    // Unknown prose is insufficient evidence that *every* failure has the same cause.
    private static readonly Regex OnlyStatusRemains = new(
        @"^(?:[\s\p{P}\d]|\b(?:fail|failed|failure|failures|pass|passed|test|tests|all|only|with|due|to|because|of|were|are|the|and|from|write|writes|refused|refusals|errors?)\b)*$",
        Options, MatchTimeout);
    private static readonly Regex AllFailures = new(
        @"\b(?:all (?:\d+ )?(?:test )?failures|only (?:\d+ )?(?:test )?failures)\b\s*(?:(?:are|were|due to|caused by)\s*)?[:=-]?\s*",
        Options, MatchTimeout);
    private static readonly Regex ViteGeneratedOutput = new(
        @"[\\/]node_modules[\\/]\.vite-temp[\\/][^\\/]*\.timestamp-[^\\/]+\.mjs$", Options, MatchTimeout);

    internal static bool TryFindExternalWrite(TaskSpec task, TaskVerificationRecord verification, out string target)
    {
        target = string.Empty;
        if (!WorkerResultBlockers.TryFindTests(verification, out var tests) ||
            string.IsNullOrWhiteSpace(verification.WorkingDirectory))
            return false;

        try
        {
            var workingDirectory = Path.GetFullPath(verification.WorkingDirectory);
            var worktree = Path.GetFullPath(task.LastDispatch?.WorkingDirectory ?? workingDirectory);
            var sandbox = Path.Combine(worktree, ".mcg-sandbox");
            var matches = Refusal.Matches(tests).Concat(DotnetRefusal.Matches(tests)).ToArray();
            if (matches.Length == 0) return false;
            foreach (var match in matches)
            {
                if (!Qualifies(match, task, workingDirectory, worktree, sandbox, out var path)) return false;
                target = path;
            }
            // An explicit all-failures statement may follow a command name or test-count summary.
            // Otherwise the entire tests field must contain only the closed diagnostic grammar.
            var all = AllFailures.Match(tests);
            var causes = all.Success ? tests[(all.Index + all.Length)..] : tests;
            if (Refusal.Matches(causes).Count + DotnetRefusal.Matches(causes).Count == 0 ||
                !OnlyStatusRemains.IsMatch(DotnetRefusal.Replace(Refusal.Replace(causes, ""), "")))
                return false;

            // Preserve assertion/mixed failures even when the summary omitted their details.
            foreach (var line in DispatchEvidenceSummaries.EnumerateEvidenceLines(verification, true, true))
            {
                var refusals = Refusal.Matches(line).Concat(DotnetRefusal.Matches(line)).ToArray();
                var remainder = DotnetRefusal.Replace(Refusal.Replace(line, ""), "");
                if (OtherFault.IsMatch(remainder) || Permission.IsMatch(remainder)) return false;
                // A second, unqualified refusal is a mixed failure even if omitted from tests:.
                if (Permission.IsMatch(line) && (refusals.Length == 0 ||
                    refusals.Any(match => !Qualifies(match, task, workingDirectory, worktree, sandbox, out _))))
                    return false;
            }
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException or RegexMatchTimeoutException)
        {
            return false;
        }
    }

    private static bool Qualifies(Match match, TaskSpec task, string workingDirectory,
        string worktree, string sandbox, out string path)
    {
        path = Path.GetFullPath(match.Groups["path"].Value, workingDirectory);
        if (IsWithin(path, worktree) || IsWithin(path, sandbox)) return false;
        if (!match.Groups["operation"].Value.Equals("open", StringComparison.OrdinalIgnoreCase)) return true;
        var flags = match.Groups["flags"].Value;
        if (flags.Length > 0) return flags.IndexOfAny(['w', 'a', '+']) >= 0;
        // Vite bundles its config to this generated output. A generic open may be a read,
        // even at Low integrity (ACL denials and MIC denials are not interchangeable).
        return task.LastDispatch?.SandboxLowIntegrity == true && ViteGeneratedOutput.IsMatch(path);
    }

    private static bool IsWithin(string path, string root)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        root = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return path.Equals(root, comparison) || path.StartsWith(root + Path.DirectorySeparatorChar, comparison);
    }
}
