using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each fact owns its temporary repository. Census windows use the current
// test identity without replacing a process-wide observer or environment variable.
public sealed class FastForwardEqualCommitSkipTests : GoalWorktreeTestBase
{
    [Fact]
    public void FreshAndExistingWorktreeOnBase_SkipStatusAndMerge()
    {
        var repo = CreateSeededRepository();
        try
        {
            var goalId = GoalId.New();
            var baseHead = Head(repo);
            var seed = File.ReadAllBytes(Path.Combine(repo, "seed.txt"));

            // Exercise both the post-create and existing-worktree Ensure call sites.
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var (path, launches) = EnsureWithLaunches(repo, goalId);

                Assert.Equal(baseHead, Head(path));
                Assert.Equal(seed, File.ReadAllBytes(Path.Combine(path, "seed.txt")));
                Assert.Equal(0, Count(launches, "status"));
                Assert.Equal(0, Count(launches, "merge"));
            }
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Fact]
    public void BehindBase_WithWarmReaders_StillFastForwards()
    {
        var repo = CreateSeededRepository();
        try
        {
            var goalId = GoalId.New();
            var path = GoalWorktrees.Ensure(repo, goalId);
            Assert.Equal(path, GoalWorktrees.Ensure(repo, goalId));
            var oldHead = Head(path);
            CommitFile(repo, "landed-fix.txt", "fix on base");
            var baseHead = Head(repo);
            Assert.NotEqual(oldHead, baseHead);
            Assert.False(File.Exists(Path.Combine(path, "landed-fix.txt")));

            var (actualPath, launches) = EnsureWithLaunches(repo, goalId);

            Assert.Equal(path, actualPath);
            Assert.Equal(baseHead, Head(path));
            Assert.Equal("fix on base", File.ReadAllText(Path.Combine(path, "landed-fix.txt")));
            Assert.True(Count(launches, "status") > 0);
            Assert.True(Count(launches, "merge") > 0);
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Fact]
    public void DivergedWorktree_KeepsItsCommitAndFiles()
    {
        var repo = CreateSeededRepository();
        try
        {
            var goalId = GoalId.New();
            var path = GoalWorktrees.Ensure(repo, goalId);
            CommitFile(path, "goal-work.txt", "developer work");
            var ownHead = Head(path);
            CommitFile(repo, "landed-fix.txt", "fix on base");
            Assert.NotEqual(ownHead, Head(repo));

            var (actualPath, launches) = EnsureWithLaunches(repo, goalId);

            Assert.Equal(path, actualPath);
            Assert.Equal(ownHead, Head(path));
            Assert.Equal("developer work", File.ReadAllText(Path.Combine(path, "goal-work.txt")));
            Assert.False(File.Exists(Path.Combine(path, "landed-fix.txt")));
            Assert.True(Count(launches, "status") > 0);
            Assert.True(Count(launches, "merge") > 0);
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Fact]
    public void DirtyWorktreeBehindBase_KeepsHeadAndUncommittedBytes()
    {
        var repo = CreateSeededRepository();
        try
        {
            var goalId = GoalId.New();
            var path = GoalWorktrees.Ensure(repo, goalId);
            var oldHead = Head(path);
            var dirtyBytes = new byte[] { 0x64, 0x69, 0x72, 0x74, 0x79, 0x0d, 0x0a };
            File.WriteAllBytes(Path.Combine(path, "seed.txt"), dirtyBytes);
            CommitFile(repo, "landed-fix.txt", "fix on base");
            Assert.NotEqual(oldHead, Head(repo));
            Assert.NotEmpty(RunGitOutput(path, "status", "--porcelain"));

            var (actualPath, launches) = EnsureWithLaunches(repo, goalId);

            Assert.Equal(path, actualPath);
            Assert.Equal(oldHead, Head(path));
            Assert.Equal(dirtyBytes, File.ReadAllBytes(Path.Combine(path, "seed.txt")));
            Assert.False(File.Exists(Path.Combine(path, "landed-fix.txt")));
            Assert.True(Count(launches, "status") > 0);
            Assert.Equal(0, Count(launches, "merge"));
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Fact]
    public void UnresolvableBase_StillChecksStatusAndAttemptsMerge()
    {
        var repo = CreateSeededRepository();
        try
        {
            var goalId = GoalId.New();
            var path = GoalWorktrees.Ensure(repo, goalId);
            var oldHead = Head(path);
            var seed = File.ReadAllBytes(Path.Combine(path, "seed.txt"));
            RunGit(repo, "checkout", "--detach");
            Assert.Empty(RunGitOutput(repo, "branch", "--show-current").Trim());
            Assert.NotEqual(0, RunGitExitCode(repo, "rev-parse", "--verify", "refs/heads/missing-base"));
            Assert.Empty(RunGitOutput(path, "status", "--porcelain"));

            var (actualPath, launches) = EnsureWithLaunches(repo, goalId, "missing-base");

            Assert.Equal(path, actualPath);
            Assert.Equal(oldHead, Head(path));
            Assert.Equal(seed, File.ReadAllBytes(Path.Combine(path, "seed.txt")));
            // Positive control: the same observer used for absence assertions sees both launches.
            Assert.True(Count(launches, "status") > 0);
            Assert.True(Count(launches, "merge") > 0);
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    private static string Head(string repo) => RunGitOutput(repo, "rev-parse", "HEAD").Trim();

    private static void CommitFile(string repo, string name, string content)
    {
        File.WriteAllText(Path.Combine(repo, name), content);
        RunGit(repo, "add", "--", name);
        RunGit(repo, "commit", "-m", "Commit " + name);
    }

    private static int Count(HostCapacitySlotGitCensus.GitLaunchDelta launches, string command) =>
        launches.Subcommands.TryGetValue(command, out var count) ? count : 0;

    private static (string Path, HostCapacitySlotGitCensus.GitLaunchDelta Launches) EnsureWithLaunches(
        string repo, GoalId goalId, string? trunkBranch = null)
    {
        var identity = HostCapacitySlotTimingWriter.CurrentTestName();
        Assert.NotEqual("<unknown>", identity);
        HostCapacitySlotGitCensus.EnsureInstalled(nameof(FastForwardEqualCommitSkipTests));
        var window = Assert.IsType<HostCapacitySlotGitCensus.Window>(HostCapacitySlotGitCensus.Shared.Open(identity));
        string path;
        HostCapacitySlotGitCensus.GitLaunchDelta? launches;
        try
        {
            path = GoalWorktrees.Ensure(repo, goalId, trunkBranch: trunkBranch);
        }
        finally
        {
            launches = window.Close();
        }
        return (path, Assert.IsType<HostCapacitySlotGitCensus.GitLaunchDelta>(launches));
    }
}
