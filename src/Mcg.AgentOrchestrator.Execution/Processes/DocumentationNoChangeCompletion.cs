using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

// A repository-evidence route, not semantic approval of the documentation.
internal static class DocumentationNoChangeCompletion
{
    internal static bool TryAccept(Goal goal, TaskSpec task, string worktreePath, string head, bool clean,
        bool hasRelevantCommitAfterDispatch, string standardOutput,
        Func<string, string[], GitCli.GitResult> runGit, out string marker, out string? declineCode)
    {
        marker = string.Empty;
        declineCode = null;
        if (task.RequiredRole != AgentRole.Developer) return Decline("doc-not-developer", out declineCode);
        var dispatch = task.LastDispatch;
        if (task.LatestRetryAt is null && task.CriterionRetryCount == 0 && task.CriterionRetryFeedback.Count == 0 &&
            !task.DispatchHistory.Any(previous => dispatch is not null && previous.DispatchedAt < dispatch.DispatchedAt))
            return Decline("doc-not-retry-or-resume", out declineCode);
        if (!clean) return Decline("doc-dirty", out declineCode);
        if (hasRelevantCommitAfterDispatch) return Decline("doc-commit-after-dispatch", out declineCode);
        if (!FullId(dispatch?.BaseCommit) || !string.Equals(dispatch!.BaseCommit, head, StringComparison.OrdinalIgnoreCase))
            return Decline("doc-head-mismatch", out declineCode);
        if (!WorkerResultParser.TryParseResult(standardOutput, out var result, out _))
            return Decline("doc-no-worker-result", out declineCode);
        if (!IsField("files", "none") || !IsField("commit", "none") || !IsField("assigned_scope_complete", "true"))
            return Decline("doc-result-not-no-change", out declineCode);
        if (result.BlockersStatus != WorkerResultParser.BlockersStatus.None)
            return Decline("doc-blockers", out declineCode);
        if (result.TestsStatus != WorkerResultParser.TestsStatus.Deferred)
            return Decline("doc-tests-not-deferred", out declineCode);
        var tests = result.Fields["tests"];
        if (tests.Split([';', '\r', '\n']).Any(part => DeveloperDeferredTestClassNames.Parse("deferred - " + part).Count > 0) ||
            DeveloperDeferredTestClassNames.Parse(tests).Count > 0 ||
            GoDeveloperNoChangeCompletion.ParseTestIdentities(tests).Count > 0 || ClaimsTests(standardOutput))
            return Decline("doc-tests-claimed", out declineCode);

        var scope = DocumentationScopePaths.FromText(task.Description);
        if (scope.Count == 0) return Decline("doc-scope-unresolved", out declineCode);
        string? baseline;
        try { baseline = WorkerBuildProjectScope.PinnedGoalBaseline(goal); }
        catch (InvalidOperationException) { return Decline("documentation-base-unavailable", out declineCode); }
        if (!FullId(baseline)) return Decline("documentation-base-unavailable", out declineCode);

        var observedHead = Read("rev-parse", "--verify", "--quiet", "HEAD^{commit}");
        if (observedHead is null || !FullId(observedHead.Trim()) ||
            !string.Equals(observedHead.Trim(), head, StringComparison.OrdinalIgnoreCase))
            return Decline("doc-head-mismatch", out declineCode);
        var observedBase = Read("rev-parse", "--verify", "--quiet", baseline + "^{commit}");
        if (observedBase is null || !string.Equals(observedBase.Trim(), baseline, StringComparison.OrdinalIgnoreCase) ||
            baseline!.Length != head.Length)
            return Decline("documentation-base-unavailable", out declineCode);

        var names = Read("diff", "--name-status", "-z", "--no-renames", baseline!, head, "--");
        if (names is null || !Pairs(names, out var entries)) return Decline("doc-diff-unavailable", out declineCode);
        if (entries.Length == 0) return Decline("doc-diff-empty", out declineCode);
        // Name-status supplies the authoritative path set. Raw metadata is also
        // needed to exclude mode-only changes, symlinks and gitlinks at either end.
        var raw = Read("diff", "--raw", "-z", "--no-renames", "--no-abbrev", baseline!, head, "--");
        if (raw is null || !Pairs(raw, out var metadata) || metadata.Length != entries.Length)
            return Decline("doc-diff-unavailable", out declineCode);
        for (var i = 0; i < entries.Length; i++)
        {
            var (status, path) = entries[i];
            var (header, rawPath) = metadata[i];
            var parts = header.TrimStart(':').Split(' ');
            if (!header.StartsWith(':') || parts.Length != 5 || rawPath != path || parts[4] != status ||
                parts[0].Length != 6 || parts[1].Length != 6 || !FullId(parts[2]) || !FullId(parts[3]))
                return Decline("doc-diff-unavailable", out declineCode);
            if (status is not ("A" or "M" or "D") || !DocumentationPath(path) ||
                !RegularOrAbsent(parts[0]) || !RegularOrAbsent(parts[1]) ||
                (status == "M" && parts[2] == parts[3]))
                return Decline("doc-non-documentation-paths", out declineCode);
        }
        if (entries.Any(entry => !scope.Any(path => DocumentationScopePaths.Covers(path, entry.Path))))
            return Decline("doc-unrelated-paths", out declineCode);
        if (scope.Any(path => !entries.Any(entry => entry.Status is "A" or "M" &&
                DocumentationScopePaths.Covers(path, entry.Path))))
            return Decline("doc-scope-unmet", out declineCode);
        marker = $"DOCUMENTATION_NO_CHANGE_ACCEPTED candidate_sha={head} accepted_base={baseline} changed_paths=" +
            string.Join(",", entries.Select(entry => entry.Path).Order(StringComparer.Ordinal).Select(Uri.EscapeDataString));
        return true;

        bool IsField(string key, string expected) => result.Fields.TryGetValue(key, out var value) &&
            string.Equals(value.Trim(), expected, StringComparison.OrdinalIgnoreCase);
        string? Read(params string[] arguments)
        {
            try
            {
                var response = runGit(worktreePath, arguments);
                return response.Succeeded && response.ProcessStarted && !response.DrainTimedOut ? response.Output : null;
            }
            catch (Exception) { return null; } // Unavailable evidence cannot establish implementation.
        }
    }

    private static bool FullId(string? value) => value is { Length: 40 or 64 } && value.All(Uri.IsHexDigit);
    private static bool RegularOrAbsent(string mode) => mode is "000000" or "100644" or "100755";
    private static bool DocumentationPath(string path)
    {
        path = path.Replace('\\', '/');
        while (path.StartsWith("./", StringComparison.Ordinal)) path = path[2..];
        // Classify is deliberately broader (gate triggers/prose parsing); use its
        // documented docs/.md boundary without its unrelated dispatch decisions.
        return path.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("docs/", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith(".agents/skills/", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ClaimsTests(string output)
    {
        var withoutNegations = Regex.Replace(output,
            @"\b(?:no tests? (?:were )?(?:run|executed)|tests? (?:were )?not (?:run|executed))\b", "",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return FreshVerificationEvidence.HasNonZeroTestCount(output) || Regex.IsMatch(withoutNegations,
            @"\btests?\s+(?:(?:all|were|have|has)\s+)*(?:pass(?:ed)?|failed|succeeded|executed|ran|run)\b|\b(?:ran|executed|passed|failed)\s+(?:\d+\s+)?tests?\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static bool Pairs(string output, out (string Status, string Path)[] entries)
    {
        entries = [];
        if (output.Length == 0) return true;
        if (!output.EndsWith('\0')) return false;
        var tokens = output[..^1].Split('\0');
        if (tokens.Length % 2 != 0 || tokens.Any(string.IsNullOrEmpty)) return false;
        entries = Enumerable.Range(0, tokens.Length / 2).Select(i => (tokens[i * 2], tokens[i * 2 + 1])).ToArray();
        return true;
    }

    private static bool Decline(string code, out string? declineCode)
    {
        declineCode = code;
        return false;
    }
}
