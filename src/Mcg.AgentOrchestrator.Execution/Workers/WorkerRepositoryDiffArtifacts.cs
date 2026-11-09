using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

// Owns supplemental candidate evidence for declared repositories and its refresh lifecycle.
internal static class WorkerRepositoryDiffArtifacts
{
    internal static IReadOnlyList<string> Write(Goal goal, string contextDirectory,
        WorkerGitContext gitContext, int patchMaxChars, bool preserveCompleteArtifacts)
    {
        DeleteStaleArtifacts(contextDirectory);
        if (goal.Repositories is not { } repositories) return [];

        var names = new List<string>();
        foreach (var key in repositories.DeclaredOrder.Where(key => key != "default"))
        {
            var checkoutRoot = repositories.CheckoutRoots[key];
            var worktree = GoalRepositoryWorktreeLayout.WorktreePath(checkoutRoot, goal.Id, key);
            var summaryName = $"diff-summary.{key}.md";
            var patchName = $"diff.{key}.patch";
            if (!Directory.Exists(worktree))
            {
                var candidateRef = "refs/heads/" + GoalRepositoryWorktreeLayout.BranchName(goal.Id, key);
                var candidate = GitCli.Run(checkoutRoot, "show-ref", "--verify", "--quiet", candidateRef);
                if (candidate.ProcessStarted && !candidate.DrainTimedOut && candidate.ExitCode == 1)
                {
                    // Preparation precedes lazy provisioning, including for read-only workers.
                    var notice = $"repository={key}; not yet provisioned; no candidate diff is available. " +
                        $"Expected goal worktree: {worktree}; candidate ref: {candidateRef}";
                    File.WriteAllText(Path.Combine(contextDirectory, summaryName), notice);
                    File.WriteAllText(Path.Combine(contextDirectory, patchName), notice);
                    names.Add(summaryName);
                    names.Add(patchName);
                    continue;
                }
                RequireComplete(key, "resolve candidate branch", candidate);
                throw new InvalidOperationException($"repository={key}; missing goal worktree: {worktree}");
            }

            var branchResult = GitCli.Run(checkoutRoot, "symbolic-ref", "--quiet", "HEAD");
            RequireComplete(key, "resolve integration branch", branchResult);
            var integrationRef = branchResult.Output.Trim();
            var branch = integrationRef.StartsWith("refs/heads/", StringComparison.Ordinal) ? integrationRef[11..] : string.Empty;
            if (branch.Length == 0)
                throw new InvalidOperationException($"repository={key}; integration branch is empty at {checkoutRoot}");

            var patch = GitCli.Run(worktree, "diff", "--binary", "--no-ext-diff", "--no-textconv", "--no-color",
                $"{integrationRef}...HEAD", "--");
            RequireComplete(key, $"read candidate diff against {branch}", patch);
            File.WriteAllText(Path.Combine(contextDirectory, summaryName),
                gitContext.BuildDiffSummary(worktree, preserveCompleteArtifacts));
            File.WriteAllText(Path.Combine(contextDirectory, patchName),
                WorkerContextHelpers.TrimArtifactBlock(patch.Output, patchMaxChars, preserveCompleteArtifacts));
            names.Add(summaryName);
            names.Add(patchName);
        }
        return names;
    }

    private static void RequireComplete(string key, string operation, GitCli.GitResult result)
    {
        if (result.Succeeded && result.ProcessStarted && !result.DrainTimedOut) return;
        var error = result.Error.ReplaceLineEndings("\n").Split('\n')[0];
        throw new InvalidOperationException($"repository={key}; cannot {operation}; " +
            $"exit={result.ExitCode}; started={result.ProcessStarted}; drainTimedOut={result.DrainTimedOut}; {error}");
    }

    internal static void DeleteStaleArtifacts(string directory)
    {
        foreach (var path in Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly))
            if (IsSupplementalName(Path.GetFileName(path))) File.Delete(path);
    }

    internal static bool IsSupplementalName(string name) => KeyOf(name) is not null;

    private static string? KeyOf(string name)
    {
        var key = name.Length > 16 && name.StartsWith("diff-summary.", StringComparison.Ordinal) && name.EndsWith(".md", StringComparison.Ordinal)
            ? name[13..^3]
            : name.Length > 11 && name.StartsWith("diff.", StringComparison.Ordinal) && name.EndsWith(".patch", StringComparison.Ordinal)
                ? name[5..^6] : null;
        return key != "default" && GoalRepositorySet.IsCanonicalKey(key) ? key : null;
    }

    internal static string Summary(string name) => name.EndsWith(".patch", StringComparison.Ordinal)
        ? $"Repository {KeyOf(name)} committed candidate diff against its integration branch base (branch...HEAD)."
        : $"Compact git status, changed files, and diff stat for repository {KeyOf(name)}'s goal worktree.";

    internal static IEnumerable<string> IndexLines(IReadOnlyList<string>? names) =>
        (names ?? []).Select(name => $"- {name}: {Summary(name)}");

    internal static IEnumerable<string> BrokerLines(IReadOnlyList<string> names) => names.Count == 0 ? [] :
        ["- repository-diff", $"  Artifacts: {string.Join(", ", names)}",
            "  Use for: reviewing every declared repository's current candidate, including committed changes.",
            "  Failure handling: missing repository evidence blocks review; report the missing artifact."];

    internal static IEnumerable<string> BudgetLines(IReadOnlyList<string> names, int patchMaxChars) =>
        names.Select(name => $"- {name} retrieval budget: " +
            $"{(name.EndsWith(".patch", StringComparison.Ordinal) ? patchMaxChars : WorkerGitContext.DiffSummaryRetrievalMaxChars)} chars.");
}
