using System.Diagnostics;
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
            var goal = setup.Goal;
            var worktree = GoalWorktrees.WorktreePath(repo, goal.Id);
            Assert.Empty(MatchingRefReads(launches, worktree, "HEAD"));
            Assert.Equal(expectedLaunches, MatchingRefReads(launches, repo, "main").Count());

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
        var branch = GoalWorktrees.BranchName(fixture.Goal.Id);
        var worktree = GoalWorktrees.WorktreePath(fixture.Repository, fixture.Goal.Id);
        Assert.Empty(MatchingRefReads(launches, worktree, "HEAD"));
        Assert.Empty(MatchingRefReads(launches, fixture.Repository, branch));
        Assert.Empty(MatchingRefReads(launches, fixture.Repository, "main"));

        Assert.NotEqual(fixture.Main, fixture.Candidate);
        Assert.Equal(ReadFromGit(fixture.Repository, branch), fixture.Candidate);
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

            var expected = RunRefReadWithGit(worktree, revision);
            Assert.Equal(succeeds, expected.Succeeded);
            var (actual, launches) = ObserveGitLaunches(() => succeeds
                ? FixtureRefFileReader.Read(worktree, revision)
                : Assert.Throws<InvalidOperationException>(() => FixtureRefFileReader.Read(worktree, revision)).Message);
            Assert.Equal(succeeds ? expected.Output.Trim() : $"git rev-parse {revision} failed: {expected.Error}", actual);
            Assert.Single(launches);
            Assert.Single(MatchingRefReads(launches, worktree, revision));
        }
        finally
        {
            LandingExecutorTests.TryDeleteDirectory(repo);
        }
    }

    private static (T Value, List<GitLaunch> Launches) ObserveGitLaunches<T>(Func<T> action)
    {
        var launches = new List<GitLaunch>();
        var previous = GitCli.StartedInfoObserver;
        GitCli.StartedInfoObserver = (ProcessStartInfo info) =>
        {
            launches.Add(new GitLaunch(info.WorkingDirectory, info.ArgumentList.ToArray()));
            previous?.Invoke(info);
        };
        try
        {
            return (action(), launches);
        }
        finally
        {
            GitCli.StartedInfoObserver = previous;
        }
    }

    private static IEnumerable<GitLaunch> MatchingRefReads(List<GitLaunch> launches, string repo, string revision)
    {
        string[] arguments = [.. GitCli.HardeningArguments, "rev-parse", revision];
        return launches.Where(launch => launch.WorkingDirectory == repo && launch.Arguments.SequenceEqual(arguments));
    }

    private static string ReadFromGit(string repo, string revision)
    {
        var result = RunRefReadWithGit(repo, revision);
        Assert.True(result.Succeeded, result.Error);
        return result.Output.Trim();
    }

    private static GitCli.GitResult RunRefReadWithGit(string repo, string revision)
    {
        var (result, launches) = ObserveGitLaunches(() => GitCli.RunDirect(repo, "rev-parse", revision));
        Assert.Single(launches);
        Assert.Single(MatchingRefReads(launches, repo, revision));
        return result;
    }

    private sealed record GitLaunch(string WorkingDirectory, string[] Arguments);
}
