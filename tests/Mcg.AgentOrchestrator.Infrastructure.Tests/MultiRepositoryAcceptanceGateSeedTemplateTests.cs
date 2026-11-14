using Xunit;
using static GoalRepositoryWorktreeProvisionerFixtureSeedTests;
using Role = MultiRepositoryAcceptanceGateSeedTemplate.RepositoryRole;

// Parallel-safe: unique repository roots and AsyncLocal-filtered, chained launch observation.
public sealed class MultiRepositoryAcceptanceGateSeedTemplateTests
{
    [Fact]
    public void CopyTo_WarmTemplate_CopiesIndependentCommittedPairsWithoutGit()
    {
        var root = SharedTestSupport.CreateTempDirectory();
        try
        {
            CopyPair(Path.Combine(root, "warm"));
            var first = PairPaths(Path.Combine(root, "first"));
            var second = PairPaths(Path.Combine(root, "second"));
            var launches = new GitLaunches();
            using (launches)
            {
                CopyPair(Path.Combine(root, "first"));
                CopyPair(Path.Combine(root, "second"));
            }
            Assert.Empty(launches.Commands);

            // A real command proves the same observer can detect a launch.
            var control = new GitLaunches();
            using (control)
                RealGit(first.Home, "rev-parse", "HEAD");
            Assert.Equal(new[] { "rev-parse" }, control.Commands);

            AssertPair(first);
            AssertPair(second);
            var homeHead = RealGit(second.Home, "rev-parse", "HEAD");
            var secondHead = RealGit(second.Second, "rev-parse", "HEAD");
            Assert.NotEqual(homeHead, GoalRepositoryWorktreeProvisionerFixture.CommitChange(first.Home));
            Assert.NotEqual(secondHead, GoalRepositoryWorktreeProvisionerFixture.CommitChange(first.Second));
            SharedTestSupport.RemoveTempDirectory(Path.Combine(root, "first"));

            // Writes and disposal of one pair cannot change another pair or the template.
            var third = CopyPair(Path.Combine(root, "third"));
            foreach (var pair in new[] { second, third })
            {
                AssertPair(pair);
                Assert.Equal(homeHead, RealGit(pair.Home, "rev-parse", "HEAD"));
                Assert.Equal(secondHead, RealGit(pair.Second, "rev-parse", "HEAD"));
            }
        }
        finally
        {
            SharedTestSupport.RemoveTempDirectory(root);
        }
    }

    [Fact]
    public void Constructor_WarmTemplate_UsesOnlyRemainingGoalSetupLaunches()
    {
        // Home worktree: 1; crosswalk provisioning: 7; change commits: 3 each;
        // verification record: 0. The three shared setup commits would add 6.
        const int maximumGitLaunches = 14;
        var root = SharedTestSupport.CreateTempDirectory();
        MultiRepositoryAcceptanceGateFixture? fixture = null;
        try
        {
            CopyPair(Path.Combine(root, "warm"));
            var launches = new GitLaunches();
            using (launches)
                fixture = new MultiRepositoryAcceptanceGateFixture();

            var commands = launches.Commands;
            Assert.True(commands.Length <= maximumGitLaunches,
                $"Expected at most {maximumGitLaunches} git launches; got {commands.Length}: {string.Join(", ", commands)}");
            Assert.Equal(2, commands.Count(command => command == "commit"));
        }
        finally
        {
            try { fixture?.Dispose(); }
            finally { SharedTestSupport.RemoveTempDirectory(root); }
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CopyTo_CopiedRepository_CanPublishAndCompensateLanding(bool home)
    {
        var root = SharedTestSupport.CreateTempDirectory();
        try
        {
            var repository = Path.Combine(root, "checkout");
            MultiRepositoryAcceptanceGateSeedTemplate.CopyTo(repository, home ? Role.Home : Role.Second);
            // Force the copied file's Windows ctime to differ from the template's index,
            // even when the template and this copy were created in the same clock tick.
            if (OperatingSystem.IsWindows())
            {
                var copiedFile = Path.Combine(repository, "example.txt");
                var creationTime = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                File.SetCreationTimeUtc(copiedFile, creationTime);
                Assert.Equal(creationTime, File.GetCreationTimeUtc(copiedFile));
            }
            var startingTip = RealGit(repository, "rev-parse", "HEAD");
            var worktree = Path.Combine(root, "goal");
            RealGit(repository, "worktree", "add", "-b", "goal", worktree, "main");
            var landedTip = GoalRepositoryWorktreeProvisionerFixture.CommitChange(worktree);

            // Match publication: observe without refreshing the copied index, then move the
            // branch before synchronizing the checkout. A plain status refresh hides this path.
            Assert.Equal("", RealGit(repository, "--no-optional-locks", "status", "--porcelain"));
            RealGit(repository, "update-ref", "refs/heads/main", landedTip, startingTip);
            RealGit(repository, "read-tree", "-m", "-u", startingTip, landedTip);
            Assert.Equal("changed\n", File.ReadAllText(Path.Combine(repository, "example.txt")));

            RealGit(worktree, "revert", "--no-edit", landedTip);
            var compensatingTip = RealGit(worktree, "rev-parse", "HEAD");
            RealGit(repository, "update-ref", "refs/heads/main", compensatingTip, landedTip);
            RealGit(repository, "read-tree", "-m", "-u", landedTip, compensatingTip);
            Assert.Equal("seed\n", File.ReadAllText(Path.Combine(repository, "example.txt")));
            Assert.Equal("", RealGit(repository, "status", "--porcelain"));
        }
        finally
        {
            SharedTestSupport.RemoveTempDirectory(root);
        }
    }

    private static (string Home, string Second) PairPaths(string root) =>
        (Path.Combine(root, "home"), Path.Combine(root, "second"));

    private static (string Home, string Second) CopyPair(string root)
    {
        var pair = PairPaths(root);
        MultiRepositoryAcceptanceGateSeedTemplate.CopyTo(pair.Home, Role.Home);
        MultiRepositoryAcceptanceGateSeedTemplate.CopyTo(pair.Second, Role.Second);
        return pair;
    }

    private static void AssertPair((string Home, string Second) pair)
    {
        foreach (var repository in new[] { pair.Home, pair.Second })
        {
            Assert.Equal("", RealGit(repository, "status", "--porcelain"));
            Assert.Equal("main", RealGit(repository, "symbolic-ref", "--short", "HEAD"));
            Assert.Equal("Worktree Provisioner Tests", RealGit(repository, "config", "--local", "user.name"));
            Assert.Equal("provisioner@example.com", RealGit(repository, "config", "--local", "user.email"));
            Assert.Equal("false", RealGit(repository, "config", "--local", "commit.gpgsign"));
            Assert.Equal("false", RealGit(repository, "config", "--local", "core.autocrlf"));
            Assert.Equal("seed\n", File.ReadAllText(Path.Combine(repository, "example.txt")));
            Assert.Equal(".orchestrator/\n", File.ReadAllText(Path.Combine(repository, ".gitignore")));
            Assert.Equal(".orchestrator/", RealGit(repository, "show", "HEAD:.gitignore"));
            Assert.Equal("Ignore case-owned runtime state",
                RealGit(repository, "log", "-1", "--format=%s", "--", ".gitignore"));
        }
        var manifest = MultiRepositoryAcceptanceGateFixture.Manifest("home check", "--info");
        Assert.Equal(manifest, File.ReadAllText(Path.Combine(pair.Home, "config", "acceptance-manifest.json")));
        Assert.Equal(manifest, RealGit(pair.Home, "show", "HEAD:config/acceptance-manifest.json"));
        Assert.Equal("Seed home manifest", RealGit(pair.Home, "log", "-1", "--format=%s"));
        Assert.False(File.Exists(Path.Combine(pair.Second, "config", "acceptance-manifest.json")));
        Assert.Equal("", RealGit(pair.Second, "ls-files", "config/acceptance-manifest.json"));
    }
}
