using Xunit;
using Fixture = GoalRepositoryWorktreeProvisionerFixture;
using static GoalRepositoryWorktreeProvisionerFixtureSeedTests;

// Parallel-safe: unique repositories and AsyncLocal-filtered, chained launch observation.
public sealed class GoalRepositoryWorktreeProvisionerFixtureRefReadTests
{
    [Fact]
    public void Run_PlainRefs_LaunchesRealGitForEachRead()
    {
        using var fixture = new Fixture();
        var path = fixture.CheckoutPath;
        var head = RealGit(path, "rev-parse", "HEAD");
        var branch = RealGit(path, "rev-parse", "refs/heads/main");
        using var launches = new GitLaunches();
        using var answers = InfrastructureTestSupport.CountGitProbeRefFileAnswers();

        Assert.Equal(head, Fixture.Run(path, "rev-parse", "HEAD"));
        Assert.Equal(branch, Fixture.Run(path, "rev-parse", "refs/heads/main"));

        Assert.Equal(new[] { "rev-parse", "rev-parse" }, launches.Commands);
        Assert.Equal(0, answers.Count);
    }

    [Theory]
    [InlineData("rev-parse", "--git-path", "HEAD")]
    [InlineData("rev-parse", "--verify", "HEAD")]
    [InlineData("rev-parse", "main")]
    [InlineData("rev-parse", "HEAD", "refs/heads/main")]
    [InlineData("for-each-ref", "--format=%(refname)", "refs/heads")]
    public void Run_NonPlainCommands_DelegatesToRealGit(params string[] arguments)
    {
        using var fixture = new Fixture();
        var expected = RealGit(fixture.CheckoutPath, arguments);
        using var launches = new GitLaunches();
        using var answers = InfrastructureTestSupport.CountGitProbeRefFileAnswers();

        Assert.Equal(expected, Fixture.Run(fixture.CheckoutPath, arguments));

        Assert.Equal(new[] { arguments[0] }, launches.Commands);
        Assert.Equal(0, answers.Count);
    }

    [Fact]
    public void Run_QuotedConfig_DelegatesToRealGit()
    {
        using var fixture = new Fixture();
        fixture.Git("config", "branch.main.description", "quoted section");
        var expected = RealGit(fixture.CheckoutPath, "rev-parse", "HEAD");
        using var launches = new GitLaunches();
        using var answers = InfrastructureTestSupport.CountGitProbeRefFileAnswers();

        Assert.Equal(expected, Fixture.Run(fixture.CheckoutPath, "rev-parse", "HEAD"));

        Assert.Equal(new[] { "rev-parse" }, launches.Commands);
        Assert.Equal(0, answers.Count);
    }

    [Fact]
    public void CommitChange_LinkedWorktree_LaunchesAddCommitAndHeadRead()
    {
        using var fixture = new Fixture();
        var path = Path.Combine(fixture.Root, "linked");
        fixture.Git("worktree", "add", "-b", "feature", path);
        Assert.True(File.Exists(Path.Combine(path, ".git")));
        var before = RealGit(path, "rev-parse", "HEAD");
        string after;
        using (var launches = new GitLaunches())
        using (var answers = InfrastructureTestSupport.CountGitProbeRefFileAnswers())
        {
            after = Fixture.CommitChange(path);
            Assert.Equal(new[] { "add", "commit", "rev-parse" }, launches.Commands);
            Assert.Equal(0, answers.Count);
        }

        Assert.NotEqual(before, after);
        Assert.Equal(RealGit(path, "rev-parse", "HEAD"), after);
        Assert.Equal(before, RealGit(fixture.CheckoutPath, "rev-parse", "HEAD"));
        Assert.Equal("changed\n", File.ReadAllText(Path.Combine(path, "example.txt")));
    }
}
