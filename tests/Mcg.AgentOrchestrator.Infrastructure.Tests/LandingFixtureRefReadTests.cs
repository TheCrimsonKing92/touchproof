using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class LandingFixtureRefReadTests : IClassFixture<IsolatedDotnetRootFixture>
{
    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 1)]
    public void AcceptedCandidateRecordsGitRefsWithOnlyNecessaryLaunches(bool ambiguousMain, int expectedLaunches)
    {
        var repo = LandingExecutorTests.CreateGitRepository();
        try
        {
            if (ambiguousMain)
            {
                var tag = GitCli.RunDirect(repo, "tag", "-a", "main", "-m", "ambiguous fixture ref", "refs/heads/main");
                Assert.True(tag.Succeeded, tag.Error);
                Assert.False(FixtureRefFileReader.TryRead(repo, "main", out _));
            }

            var (setup, launches) = ObserveGitLaunches(
                () => LandingExecutorTests.CreateAcceptedCandidate(repo, "src/accepted.txt"));
            AssertFixtureRefLaunches(launches, expectedLaunches);

            var goal = setup.Goal;
            var worktree = GoalWorktrees.WorktreePath(repo, goal.Id);
            Assert.True(File.Exists(Path.Combine(worktree, ".git")));
            var candidate = ReadFromGit(worktree, "HEAD");
            var main = ReadFromGit(repo, "main");
            Assert.NotEqual(main, candidate);
            if (ambiguousMain)
                Assert.NotEqual(ReadFromGit(repo, "refs/heads/main"), main);
            var entry = Assert.Single(GoalOperationJournal.Read(repo, goal.Id).Entries,
                item => item.AcceptanceOutcome == "passed");
            Assert.Equal(candidate, entry.BranchHeadSha);
            Assert.Equal(main, entry.MainHeadSha);
        }
        finally
        {
            LandingExecutorTests.TryDeleteDirectory(repo);
        }
    }

    [Fact]
    public void DefaultDeliveryFixtureReadsCandidateBranchAndMainWithoutLaunchingRefReads()
    {
        var (delivery, launches) = ObserveGitLaunches(() => new PullRequestLandingTestFixture());
        using var fixture = delivery;
        AssertFixtureRefLaunches(launches, 0);

        Assert.NotEqual(fixture.Main, fixture.Candidate);
        Assert.Equal(ReadFromGit(fixture.Repository, GoalWorktrees.BranchName(fixture.Goal.Id)), fixture.Candidate);
        Assert.Equal(ReadFromGit(fixture.Repository, "main"), fixture.Main);
    }

    [Theory]
    [InlineData("HEAD^", true)]
    [InlineData("ambiguous", true)]
    [InlineData("missing-fixture-ref", false)]
    public void DeclinedReadLaunchesGitOnceAndPreservesItsResult(string revision, bool succeeds)
    {
        var repo = LandingExecutorTests.CreateGitRepository();
        try
        {
            var (_, _, goal) = LandingExecutorTests.CreateAcceptedCandidate(repo, "src/candidate.txt");
            var worktree = GoalWorktrees.WorktreePath(repo, goal.Id);
            LandingExecutorTests.AppendCommit(worktree, "src/second.txt", "second fixture commit");
            var branch = GitCli.RunDirect(repo, "branch", "ambiguous", "main");
            Assert.True(branch.Succeeded, branch.Error);
            var tag = GitCli.RunDirect(worktree, "tag", "ambiguous", "HEAD");
            Assert.True(tag.Succeeded, tag.Error);
            Assert.False(FixtureRefFileReader.TryRead(worktree, revision, out _));

            var expected = GitCli.RunDirect(worktree, "rev-parse", revision);
            Assert.Equal(succeeds, expected.Succeeded);
            var (actual, launches) = ObserveGitLaunches(() => succeeds
                ? FixtureRefFileReader.Read(worktree, revision)
                : Assert.Throws<InvalidOperationException>(() => FixtureRefFileReader.Read(worktree, revision)).Message);
            Assert.Equal(succeeds ? expected.Output.Trim() : $"git rev-parse {revision} failed: {expected.Error}", actual);
            Assert.Equal(1, launches.Launches);
            Assert.Equal(1, launches.Subcommands["rev-parse"]);
        }
        finally
        {
            LandingExecutorTests.TryDeleteDirectory(repo);
        }
    }

    private static (T Value, HostCapacitySlotGitCensus.GitLaunchDelta Launches) ObserveGitLaunches<T>(Func<T> action)
    {
        var identity = HostCapacitySlotTimingWriter.CurrentTestName();
        Assert.NotEqual("<unknown>", identity);
        HostCapacitySlotGitCensus.EnsureInstalled(nameof(LandingFixtureRefReadTests));
        var window = Assert.IsType<HostCapacitySlotGitCensus.Window>(HostCapacitySlotGitCensus.Shared.Open(identity));
        try
        {
            return (action(), Assert.IsType<HostCapacitySlotGitCensus.GitLaunchDelta>(window.Close()));
        }
        finally
        {
            window.Close();
        }
    }

    private static void AssertFixtureRefLaunches(HostCapacitySlotGitCensus.GitLaunchDelta launches, int expected)
    {
        Assert.Contains("worktree", launches.Subcommands.Keys);
        Assert.NotNull(launches.Callers);
        var fixtureReads = launches.Callers.Where(pair => pair.Key.EndsWith("|rev-parse", StringComparison.Ordinal) &&
            HostCapacitySlotGitCensus.CallerOf(pair.Key) == "test:LandingExecutorTests.ReadGit");
        Assert.Equal(expected, fixtureReads.Sum(pair => pair.Value));
    }

    private static string ReadFromGit(string repo, string revision)
    {
        var result = GitCli.RunDirect(repo, "rev-parse", revision);
        Assert.True(result.Succeeded, result.Error);
        return result.Output.Trim();
    }
}
