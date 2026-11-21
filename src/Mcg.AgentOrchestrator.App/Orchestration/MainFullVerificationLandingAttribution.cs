using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class MainFullVerificationLandingAttribution
{
    private const int MaxCommits = 50;

    internal static IReadOnlyList<MainFullVerificationFailure> Resolve(
        string checkout, string commit, string? lastPassed, IReadOnlyList<string> tests,
        IReadOnlyList<SelectedExecutionBackstopLanding> landings)
    {
        var history = GitCli.Run(checkout, "rev-list", "--first-parent", $"--max-count={MaxCommits}", commit);
        var commits = history.Succeeded && !history.DrainTimedOut
            ? history.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .TakeWhile(sha => !string.Equals(sha, lastPassed, StringComparison.OrdinalIgnoreCase))
            : [];
        return Resolve(tests, commits, sha => ReadReach(checkout, sha),
            sha => landings.LastOrDefault(landing => string.Equals(sha, landing.Commit, StringComparison.OrdinalIgnoreCase))?.GoalId);
    }

    internal static IReadOnlyList<MainFullVerificationFailure> Resolve(
        IReadOnlyList<string> tests, IEnumerable<string> newestFirst,
        Func<string, IReadOnlySet<string>?> reach, Func<string, string?> goalFor)
    {
        var failures = tests.ToDictionary(test => test, test => new MainFullVerificationFailure(test), StringComparer.Ordinal);
        var unresolved = new HashSet<string>(tests, StringComparer.Ordinal);
        foreach (var commit in newestFirst)
        {
            if (unresolved.Count == 0) break;
            var classes = reach(commit);
            if (classes is null) break;
            foreach (var test in unresolved.Where(test => ReachesClass(test, classes)).ToArray())
            {
                failures[test] = new(test, commit, goalFor(commit));
                unresolved.Remove(test);
            }
        }
        return tests.Select(test => failures[test]).ToArray();
    }

    private static bool ReachesClass(string test, IReadOnlySet<string> classes)
    {
        var method = test.LastIndexOf('.');
        if (method <= 0) return false;
        var separator = test.LastIndexOfAny(['.', '+'], method - 1);
        return classes.Contains(test[(separator + 1)..method]);
    }

    private static IReadOnlySet<string>? ReadReach(string checkout, string commit)
    {
        var changes = GitCli.Run(checkout, "diff-tree", "--root", "--first-parent", "-m", "--no-commit-id",
            "--name-only", "--no-renames", "-r", "-z", commit, "--", "*.cs");
        if (!changes.Succeeded || changes.DrainTimedOut) return null;
        var paths = changes.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        return paths.Length == 0 ? new HashSet<string>(StringComparer.Ordinal) :
            RepositoryTestReach.TryReadReachableTestClasses(checkout, paths);
    }
}
