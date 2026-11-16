using System.Text;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: unique repositories, synchronous thread-local observation and keyed census.
public sealed class GitMetadataLookupSeamParityTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SupportedForms_MatchDirectGitWithoutLaunching(bool linked)
    {
        using var fixture = new GitMetadataLookupFixture();
        var directory = linked ? fixture.Linked : fixture.Main;
        foreach (var arguments in GitMetadataLookupFixture.Forms())
        {
            var expected = GitCli.RunDirect(directory, arguments);
            Assert.True(expected.Succeeded, expected.Error);
            using var recording = new GitWorkspaceLaunchFixture.Recording();
            var actual = GitCli.Run(directory, arguments);
            GitMetadataLookupFixture.EqualBytes(expected, actual);
            Assert.Equal(0, recording.Close([]));

            using var helperLaunches = CountGitProbeLaunches();
            var helper = RunGitProbe(directory, arguments);
            Assert.True(helper.Succeeded, helper.ToString());
            Assert.Equal(expected.ExitCode, helper.ExitCode);
            Assert.Equal(Encoding.UTF8.GetBytes(expected.Output), Encoding.UTF8.GetBytes(helper.StandardOutput));
            Assert.Equal(Encoding.UTF8.GetBytes(expected.Error), Encoding.UTF8.GetBytes(helper.StandardError));
            Assert.Equal(0, helperLaunches.Count);
        }
        GitWorkspaceLaunchFixture.Git(directory, "checkout", "--detach");
        var detached = GitCli.RunDirect(directory, "branch", "--show-current");
        Assert.True(detached.Succeeded, detached.Error);
        Assert.Empty(detached.Output);
        using var detachedRecording = new GitWorkspaceLaunchFixture.Recording();
        GitMetadataLookupFixture.EqualBytes(detached, GitCli.Run(directory, "branch", "--show-current"));
        Assert.Equal(0, detachedRecording.Close([]));
        using var detachedHelperLaunches = CountGitProbeLaunches();
        var detachedHelper = RunGitProbe(directory, ["branch", "--show-current"]);
        Assert.True(detachedHelper.Succeeded, detachedHelper.ToString());
        Assert.Empty(detachedHelper.StandardOutput);
        Assert.Equal(0, detachedHelperLaunches.Count);
    }

    [Fact]
    public void WrapperEntries_UseFreshMetadataAfterRefAndHeadChanges()
    {
        using var fixture = new GitMetadataLookupFixture();
        var directory = fixture.Linked;
        var before = GitCli.RunDirect(directory, "rev-parse", "--verify", "HEAD");
        GitMetadataLookupFixture.AssertInProcess(directory, "rev-parse", "--verify", "HEAD");
        GitWorkspaceLaunchFixture.Git(directory, "-c", "user.name=Lookup Tests", "-c", "user.email=lookup@example.test",
            "-c", "commit.gpgSign=false", "commit", "--allow-empty", "-m", "move ref");
        var after = GitCli.RunDirect(directory, "rev-parse", "--verify", "HEAD");
        Assert.NotEqual(before.Output, after.Output);
        using var recording = new GitWorkspaceLaunchFixture.Recording();
        GitMetadataLookupFixture.EqualBytes(after, GitCli.Run(directory, 60_000, "rev-parse", "--verify", "HEAD"));
        GitMetadataLookupFixture.EqualBytes(after, GitCli.RunForRepository(directory, "rev-parse", "--verify", "HEAD"));
        GitMetadataLookupFixture.EqualBytes(after, GitCli.RunWithEnvironment(directory,
            new Dictionary<string, string>(), "rev-parse", "--verify", "HEAD"));
        GitMetadataLookupFixture.EqualBytes(after, GitCli.RunExecutable("git", directory, 60_000,
            "rev-parse", "--verify", "HEAD"));
        Assert.Equal(0, recording.Close([]));
    }

    [Fact]
    public void RepositoryMutation_StillLaunchesAndCreatesRef()
    {
        using var fixture = new GitMetadataLookupFixture();
        using (var recording = new GitWorkspaceLaunchFixture.Recording())
        {
            var result = GitCli.Run(fixture.Linked, "branch", "new-branch");
            Assert.True(result.Succeeded, result.Error);
            Assert.Equal(1, recording.Close("branch"));
        }
        var created = GitCli.RunDirect(fixture.Linked, "rev-parse", "--verify", "refs/heads/new-branch");
        Assert.True(created.Succeeded, created.Error);
        Assert.Equal(GitCli.RunDirect(fixture.Linked, "rev-parse", "--verify", "HEAD").Output, created.Output);
    }
}
