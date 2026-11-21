using System.Text;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

internal static class FixtureGitLaunchBudget
{
    private static readonly object ObserverLock = new();

    internal sealed record Launches(string[] Probes, GitLaunchWindowCensus.Totals Git)
    {
        // RunGitProbe reports started processes to the census. Keep the funnel's
        // fallback observations separately for attribution.
        internal long Total => Git.Total.Launches;
        internal long Count(string command) =>
            Git.Subcommands.GetValueOrDefault(command)?.Launches ?? 0;
    }

    internal static (T Value, Launches Counts) Measure<T>(string phase, Func<T> action)
    {
        lock (ObserverLock)
        {
            using var funnel = InProcessGitFunnel.Observe();
            using var census = GitLaunchWindowCensus.Install();
            using var window = census.OpenWindow(phase);
            var result = action();
            var counts = new Launches(funnel.Launched, window.Close());
            var commands = counts.Git.Subcommands.OrderBy(pair => pair.Key)
                .Select(pair => $"{pair.Key}={pair.Value.Launches}");
            TestContext.Current.TestOutputHelper?.WriteLine(
                $"{phase}: real git={counts.Total}; funnel fallbacks=[{string.Join(", ", counts.Probes)}]; launches by command=[{string.Join(", ", commands)}]");
            return (result, counts);
        }
    }

    internal static string Git(string root, params string[] arguments) =>
        AdditiveConflictGitFixture.Git(root, arguments).TrimEnd('\r', '\n');

    internal static void AssertTree(string root, string revision, string seed, params (string Path, byte[] Bytes)[] files)
    {
        var entries = Git(root, "ls-tree", "-r", seed).Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.TrimEnd('\r')).ToDictionary(line => line[(line.IndexOf('\t') + 1)..], StringComparer.Ordinal);
        foreach (var (path, bytes) in files)
            entries[path] = $"100644 blob {FixtureRepositoryWriter.HashBlob(bytes)}\t{path}";
        Assert.Equal(entries.Values.Order(StringComparer.Ordinal),
            Git(root, "ls-tree", "-r", revision).Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.TrimEnd('\r')).Order(StringComparer.Ordinal));
    }

    internal static (string Path, byte[] Bytes) Entry(string path, string content) =>
        (path, Encoding.UTF8.GetBytes(content));

    internal static void AssertWorktree(string root, string worktree, GoalId id, string head)
    {
        var branch = GoalWorktrees.BranchName(id);
        Assert.Equal(branch, Git(worktree, "symbolic-ref", "--short", "HEAD"));
        Assert.Equal(head, Git(worktree, "rev-parse", "HEAD"));
        Assert.Equal(head, Git(root, "rev-parse", $"refs/heads/{branch}"));
        var list = Git(root, "worktree", "list", "--porcelain", "-z");
        Assert.Contains($"worktree {worktree.Replace('\\', '/')}\0HEAD {head}\0branch refs/heads/{branch}\0",
            list, StringComparison.Ordinal);
        Assert.Equal("", Git(worktree, "status", "--porcelain=v1", "--untracked-files=all"));
    }

    internal static void AssertMain(string root, string head)
    {
        Assert.Equal("main", Git(root, "symbolic-ref", "--short", "HEAD"));
        Assert.Equal(head, Git(root, "rev-parse", "refs/heads/main"));
        Assert.Equal("", Git(root, "status", "--porcelain=v1", "--untracked-files=all"));
        _ = Git(root, "fsck", "--strict");
    }
}
