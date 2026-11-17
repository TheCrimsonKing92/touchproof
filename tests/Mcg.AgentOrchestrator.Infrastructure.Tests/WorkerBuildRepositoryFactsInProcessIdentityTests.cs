using Mcg.AgentOrchestrator.Infrastructure;
using LaunchRecording = InProcessGitFixture.LaunchRecording;

// Parallel-safe: fixture-owned repositories and test-identity-scoped launch windows.
public sealed class WorkerBuildRepositoryFactsInProcessIdentityTests
{
    [Theory]
    [InlineData("loose")]
    [InlineData("packed")]
    [InlineData("linked")]
    public void Identity_MatchesCombinedGitWithoutRevParse(string layout)
    {
        using var fixture = new InProcessGitFixture(layout);
        var expected = LegacyIdentity(fixture.Worktree);
        var launches = new LaunchRecording();
        Assert.Equal(expected, WorkerBuildRepositoryFacts.ReadIdentity(fixture.Worktree));
        launches.AssertRevParseCount(0);
        fixture.AssertRevisionParity();
    }

    [Theory]
    [InlineData("loose")]
    [InlineData("linked")]
    public void DetachedHead_IsSuccessfulIdentityWithNullBranch(string layout)
    {
        using var fixture = new InProcessGitFixture(layout);
        fixture.Git("checkout", "--detach", "HEAD");
        var expected = LegacyIdentity(fixture.Worktree);
        Assert.Null(expected.Branch);
        var launches = new LaunchRecording();
        var actual = WorkerBuildRepositoryFacts.ReadIdentity(fixture.Worktree);
        Assert.Equal(expected, actual);
        launches.AssertRevParseCount(0);
        var error = Assert.Throws<IOException>(() => actual.RequireBranch());
        Assert.Equal("Worker build scope Git symbolic-ref failed: HEAD is detached; worker build scope requires a checked-out branch.",
            error.Message);
    }

    [Theory]
    [InlineData("unsupported-config")]
    [InlineData("shadow-tag")]
    [InlineData("packed-only")]
    [InlineData("top-level")]
    public void AnyMissOrTopLevel_UsesOriginalCombinedProcess(string shape)
    {
        using var fixture = new InProcessGitFixture();
        if (shape == "unsupported-config") fixture.Git("config", "core.hooksPath", "unused-hooks");
        if (shape == "shadow-tag") fixture.Git("tag", "trunk");
        if (shape == "packed-only") fixture.Git("pack-refs", "--all");
        var includeTopLevel = shape == "top-level";
        var expected = LegacyIdentity(fixture.Worktree, includeTopLevel);
        var launches = new LaunchRecording();
        Assert.Equal(expected, WorkerBuildRepositoryFacts.ReadIdentity(fixture.Worktree, includeTopLevel));
        launches.AssertRevParseCount(1);
    }

    [Fact]
    public void UnbornHead_PreservesIOExceptionMessage()
    {
        using var fixture = new InProcessGitFixture(commit: false);
        var legacy = GitCli.RunForRepository(fixture.Worktree, "rev-parse", "--path-format=absolute",
            "--git-common-dir", "HEAD", "--abbrev-ref=loose", "HEAD");
        Assert.False(legacy.Succeeded);
        var launches = new LaunchRecording();
        var error = Assert.Throws<IOException>(() => WorkerBuildRepositoryFacts.ReadIdentity(fixture.Worktree));
        launches.AssertRevParseCount(1);
        Assert.Equal($"Worker build scope Git rev-parse failed (exit={legacy.ExitCode}): {legacy.Error.Trim()}", error.Message);
    }

    private static WorkerBuildRepositoryFacts.Identity LegacyIdentity(string root, bool includeTopLevel = false)
    {
        var arguments = new List<string> { "rev-parse" };
        if (includeTopLevel) arguments.Add("--show-toplevel");
        arguments.AddRange(["--path-format=absolute", "--git-common-dir", "HEAD", "--abbrev-ref=loose", "HEAD"]);
        var result = GitCli.RunForRepository(root, arguments.ToArray());
        Assert.True(result.Succeeded && !result.DrainTimedOut, result.Error);
        var lines = result.Output.TrimEnd('\r', '\n').Split('\n');
        Assert.Equal(includeTopLevel ? 4 : 3, lines.Length);
        var offset = includeTopLevel ? 1 : 0;
        var branch = lines[offset + 2].Trim();
        return new(includeTopLevel ? lines[0].Trim() : null, lines[offset].Trim(), lines[offset + 1].Trim(),
            branch == "HEAD" ? null : branch);
    }
}
