using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;
using LaunchRecording = InProcessGitFixture.LaunchRecording;

// Parallel-safe: fixture-owned repositories and test-identity-scoped launch windows.
public sealed class ConductorConflictIntegrationInProcessRefTests
{
    [Theory]
    [InlineData("loose")]
    [InlineData("packed")]
    [InlineData("linked")]
    public void RefsAndRequiredRevisions_MatchGitWithoutRevParse(string layout)
    {
        using var fixture = new InProcessGitFixture(layout);
        foreach (var revision in new[] { "HEAD", "trunk", "refs/heads/topic" })
        {
            var expected = fixture.Git("rev-parse", "--verify", "--quiet", revision);
            var launches = new LaunchRecording();
            Assert.Equal(expected, ConductorConflictIntegration.ReadReference(fixture.Worktree, revision, "trunk"));
            launches.AssertRevParseCount(0);
        }
        foreach (var revision in new[] { "HEAD", "trunk^{commit}" })
        {
            var expected = fixture.Git("rev-parse", revision);
            var launches = new LaunchRecording();
            Assert.Equal(expected, ConductorConflictIntegration.ReadRevision(fixture.Worktree, revision).Trim());
            launches.AssertRevParseCount(0);
        }
        fixture.AssertRevisionParity();
    }

    [Theory]
    [InlineData("MERGE_HEAD")]
    [InlineData("ORIG_HEAD")]
    public void AbsentPseudoRef_UsesOneProcessAndNeverResolves(string reference)
    {
        using var fixture = new InProcessGitFixture();
        var launches = new LaunchRecording();
        Assert.Null(ConductorConflictIntegration.ReadReference(fixture.Worktree, reference, "trunk",
            (_, _) => throw new InvalidOperationException("Pseudo-ref reached resolver")));
        launches.AssertRevParseCount(1);
    }

    [Fact]
    public void ForcedMiss_PreservesSuccessNullAndFailureMessages()
    {
        using var fixture = new InProcessGitFixture();
        var expected = fixture.Git("rev-parse", "HEAD");
        var misses = 0;
        string? Miss(string _, string revision) { misses++; return null; }
        var launches = new LaunchRecording();
        Assert.Equal(expected, ConductorConflictIntegration.ReadReference(fixture.Worktree, "HEAD", "trunk", Miss));
        Assert.Equal(expected, ConductorConflictIntegration.ReadRevision(fixture.Worktree, "HEAD", Miss).Trim());
        Assert.Null(ConductorConflictIntegration.ReadReference(fixture.Worktree, "refs/heads/absent", "trunk", Miss));
        launches.AssertRevParseCount(3);
        Assert.Equal(3, misses);

        var directory = Path.Combine(fixture.Worktree, "missing-directory");
        var legacy = GitCli.Run(directory, "rev-parse", "--verify", "--quiet", "HEAD");
        var error = Assert.Throws<InvalidOperationException>(() =>
            ConductorConflictIntegration.ReadReference(directory, "HEAD", "trunk", Miss));
        Assert.Equal("Could not read git reference HEAD: " + legacy.Error, error.Message);
        legacy = GitCli.Run(directory, "rev-parse", "HEAD");
        error = Assert.Throws<InvalidOperationException>(() =>
            ConductorConflictIntegration.ReadRevision(directory, "HEAD", Miss));
        Assert.Equal("git rev-parse HEAD: " + legacy.Error, error.Message);
    }
}
