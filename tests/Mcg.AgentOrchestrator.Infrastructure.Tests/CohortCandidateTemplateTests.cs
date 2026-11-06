using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: instance-owned recipes, independent roots and a test-identity census window.
public sealed class CohortCandidateTemplateTests : GoalWorktreeTestBase
{
    [Fact]
    public void WarmCopy_PreservesRefsTreesAndWorktreeIndexesWithoutWindowsRefreshes()
    {
        using var template = new CohortCandidateTemplate();
        var first = template.Copy("equivalence", 2, Prepare);
        string? secondRoot = null;
        string? reference = null;
        try
        {
            var identity = HostCapacitySlotTimingWriter.CurrentTestName();
            Assert.NotEqual("<unknown>", identity);
            HostCapacitySlotGitCensus.EnsureInstalled(nameof(CohortCandidateTemplateTests));
            var window = Assert.IsType<HostCapacitySlotGitCensus.Window>(HostCapacitySlotGitCensus.Shared.Open(identity));
            CohortCandidateTemplate.PreparedRepository second;
            HostCapacitySlotGitCensus.GitLaunchDelta? delta;
            try
            {
                second = template.Copy("equivalence", 2, Prepare);
                secondRoot = second.Repo;
            }
            finally { delta = window.Close(); }
            Assert.NotNull(delta);
            Assert.Equal(OperatingSystem.IsWindows() ? 1 : 3, delta.Launches);
            Assert.Equal(delta.Launches, delta.Subcommands["update-index"]);

            reference = CreateReducedAcceptanceCohortRepository();
            Prepare(reference, second.GoalIds);
            Assert.Equal(first.GoalIds, second.GoalIds);
            Assert.Equal(first.Revisions, second.Revisions);
            foreach (var copy in new[] { first.Repo, second.Repo })
            {
                Assert.Equal(Read(reference, "for-each-ref", "--format=%(refname) %(objectname) %(tree)"),
                    Read(copy, "for-each-ref", "--format=%(refname) %(objectname) %(tree)"));
                Assert.Equal(Read(reference, "log", "--all", "--format=%H %T %P %s"),
                    Read(copy, "log", "--all", "--format=%H %T %P %s"));
                Assert.Equal(Registrations(reference), Registrations(copy));
                Assert.Equal(Read(reference, "config", "--local", "--list"), Read(copy, "config", "--local", "--list"));
                AssertCheckout(reference, copy);
                foreach (var goal in second.GoalIds)
                {
                    var actual = GoalWorktrees.WorktreePath(copy, goal);
                    Assert.Equal(actual, GoalWorktrees.TryResolve(copy, goal));
                    AssertCheckout(GoalWorktrees.WorktreePath(reference, goal), actual);
                }
            }

            var worktree = GoalWorktrees.WorktreePath(second.Repo, second.GoalIds[0]);
            Read(worktree, "read-tree", "-m", "-u", "HEAD", "main");
            Assert.Equal(Read(second.Repo, "rev-parse", "main^{tree}"), Read(worktree, "write-tree"));
            Assert.False(File.Exists(Path.Combine(worktree, "candidate-0.txt")));
            Assert.True(File.Exists(Path.Combine(GoalWorktrees.WorktreePath(first.Repo, first.GoalIds[0]), "candidate-0.txt")));
            Assert.True(File.Exists(Path.Combine(GoalWorktrees.WorktreePath(template.TemplatePath("equivalence"), first.GoalIds[0]), "candidate-0.txt")));
        }
        finally
        {
            DeleteDirectory(first.Repo);
            if (secondRoot is not null) DeleteDirectory(secondRoot);
            if (reference is not null) DeleteDirectory(reference);
        }
    }

    private static void Prepare(string root, GoalId[] goals)
    {
        for (var index = 0; index < goals.Length; index++)
        {
            // The same hand-built fixture path, with explicit commit dates for exact ref parity.
            var worktree = GoalWorktrees.Ensure(root, goals[index]);
            var path = $"candidate-{index}.txt";
            File.WriteAllText(Path.Combine(worktree, path), $"candidate {index}");
            Read(worktree, "add", path);
            var result = GitCli.RunWithEnvironment(worktree, new Dictionary<string, string>
            {
                ["GIT_AUTHOR_DATE"] = "2026-01-01T00:00:00Z",
                ["GIT_COMMITTER_DATE"] = "2026-01-01T00:00:00Z",
                ["GIT_CONFIG_GLOBAL"] = OperatingSystem.IsWindows() ? "NUL" : "/dev/null",
                ["GIT_CONFIG_NOSYSTEM"] = "1",
                ["GIT_CONFIG_COUNT"] = "2",
                ["GIT_CONFIG_KEY_0"] = "commit.gpgSign", ["GIT_CONFIG_VALUE_0"] = "false",
                ["GIT_CONFIG_KEY_1"] = "core.hooksPath", ["GIT_CONFIG_VALUE_1"] = Path.Combine(root, "no-hooks")
            }, "commit", "-m", $"Candidate {goals[index].Value[..8]}");
            Assert.True(result.Succeeded, result.Error);
        }
    }

    private static void AssertCheckout(string expected, string actual)
    {
        foreach (var args in new[] { new[] { "symbolic-ref", "HEAD" }, ["ls-files", "-s"], ["status", "--porcelain"] })
            Assert.Equal(Read(expected, args), Read(actual, args));
        Assert.Empty(Read(actual, "status", "--porcelain"));
        foreach (var path in Read(expected, "ls-files").Split('\n', StringSplitOptions.RemoveEmptyEntries))
            Assert.Equal(File.ReadAllBytes(Path.Combine(expected, path.TrimEnd('\r'))),
                File.ReadAllBytes(Path.Combine(actual, path.TrimEnd('\r'))));
    }

    private static string Registrations(string root) => Read(root, "worktree", "list", "--porcelain")
        .Replace(root.Replace('\\', '/'), "<root>", StringComparison.Ordinal);

    private static string Read(string root, params string[] args)
    {
        var result = GitCli.Run(root, args);
        Assert.True(result.Succeeded, result.Error);
        return result.Output.TrimEnd('\r', '\n');
    }
}
