using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using LaunchRecording = InProcessGitFixture.LaunchRecording;

// Parallel-safe: fixture-owned repositories, instance delegates and test-identity-scoped launch windows.
public sealed class DispatchWorktreeCommitterInProcessHeadTests
{
    private static readonly DateTimeOffset DispatchedAt = new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("loose")]
    [InlineData("packed")]
    [InlineData("linked")]
    public void DefaultRunner_HeadMatchesGitWithoutRevParse(string layout)
    {
        using var fixture = new InProcessGitFixture(layout);
        var expected = fixture.Git("rev-parse", "HEAD");
        // Production admission expects a gitfile. This filesystem predicate also admits the
        // ordinary repository layouts; all Git operations still use the default real runner.
        var committer = new DispatchWorktreeCommitter(fileExists: path => File.Exists(path) || Directory.Exists(path));
        var launches = new LaunchRecording();
        var result = Inspect(committer, fixture);
        Assert.True(result.IsAvailable, result.GitReceipt);
        Assert.Equal(expected, result.Evidence.Head);
        Assert.Equal(fixture.Branch, result.Evidence.Branch);
        launches.AssertRevParseCount(0);
        fixture.AssertRevisionParity();
    }

    [Fact]
    public void InjectedRunner_ReceivesHeadAndAllOriginalCommands()
    {
        using var fixture = new InProcessGitFixture("linked");
        var expected = fixture.Git("rev-parse", "HEAD");
        var commands = new List<string[]>();
        var committer = new DispatchWorktreeCommitter(runGit: (directory, arguments) =>
        {
            commands.Add(arguments);
            return GitCli.Run(directory, arguments);
        }, resolveRevision: (_, _) => throw new InvalidOperationException("Injected runner was bypassed"));
        var launches = new LaunchRecording();
        var result = Inspect(committer, fixture);
        Assert.True(result.IsAvailable, result.GitReceipt);
        Assert.Equal(expected, result.Evidence.Head);
        launches.AssertRevParseCount(1);
        Assert.Equal(5, commands.Count);
        Assert.Equal(["branch", "--show-current"], commands[0]);
        Assert.Equal(["rev-parse", "HEAD"], commands[1]);
        Assert.Equal(["status", "--short", "--untracked-files=all"], commands[2]);
        Assert.Equal(["log", "--format=%H", $"--since={DispatchedAt:O}"], commands[3]);
        Assert.Equal(["log", "--name-only", "--format=", $"--since={DispatchedAt:O}"], commands[4]);
    }

    [Fact]
    public void ForcedMiss_UsesOriginalHeadProcess()
    {
        using var fixture = new InProcessGitFixture("linked");
        var expected = fixture.Git("rev-parse", "HEAD");
        var misses = 0;
        var committer = new DispatchWorktreeCommitter(resolveRevision: (directory, revision) =>
        {
            Assert.Equal(fixture.Worktree, directory);
            Assert.Equal("HEAD", revision);
            misses++;
            return null;
        });
        var launches = new LaunchRecording();
        var result = Inspect(committer, fixture);
        Assert.True(result.IsAvailable, result.GitReceipt);
        Assert.Equal(expected, result.Evidence.Head);
        Assert.Equal(1, misses);
        launches.AssertRevParseCount(1);
    }

    [Fact]
    public void UnbornHead_FallbackRetainsFailureReceipt()
    {
        using var fixture = new InProcessGitFixture(commit: false);
        var original = new DispatchWorktreeCommitter(runGit: (directory, args) => GitCli.Run(directory, args),
            fileExists: path => File.Exists(path) || Directory.Exists(path));
        var fallback = new DispatchWorktreeCommitter(resolveRevision: (_, _) => null,
            fileExists: path => File.Exists(path) || Directory.Exists(path));
        var expected = Inspect(original, fixture);
        var actual = Inspect(fallback, fixture);
        Assert.False(actual.IsAvailable);
        Assert.Equal("git-inspection-failed", actual.UnavailableReason);
        Assert.Equal(expected, actual);
    }

    private static GoalWorktreeInspectionResult Inspect(DispatchWorktreeCommitter committer, InProcessGitFixture fixture) =>
        committer.InspectGoalWorktree(fixture.Worktree, GoalId.New(), DispatchedAt, expectedBranch: fixture.Branch);
}
