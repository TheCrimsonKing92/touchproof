using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: fixture-owned repositories and AsyncLocal launch capture; no global mutations.
public sealed class GoalAcceptanceVerifierResolveGitScalarTests
{
    public static bool IsWindows => OperatingSystem.IsWindows();

    [Theory(Skip = "Requires Windows launch telemetry.", SkipUnless = nameof(IsWindows))]
    [InlineData(false)]
    [InlineData(true)]
    public void ResolveGitScalar_ProvedRevision_MatchesGitWithoutLaunch(bool linked)
    {
        using var fixture = new GitRefFileResolverParityTests.Fixture();
        var repo = fixture.Repo;
        if (linked)
        {
            repo = Path.Combine(fixture.Root, "linked");
            fixture.Git("worktree", "add", "-b", "linked-branch", repo);
            fixture.Git("commit", "--allow-empty", "-m", "main advances");
            Assert.True(File.Exists(Path.Combine(repo, ".git")));
            Assert.NotEqual(fixture.Oracle("HEAD"), Oracle(repo, "rev-parse", "HEAD"));
        }
        foreach (var revision in new[] { "HEAD", "HEAD^{tree}", "refs/heads/fixture-main", "fixture-main" })
        {
            var expected = Oracle(repo, "rev-parse", revision);
            Assert.NotNull(expected);
            Assert.True(GitRefFileResolver.TryResolve(repo, revision, out var resolved));
            Assert.Equal(expected, resolved);
            using var capture = LaunchLockTelemetry.BeginCapture();
            Assert.Equal(expected, GoalAcceptanceVerifier.ResolveGitScalar(repo, "rev-parse", revision));
            Assert.Equal(0, capture.Snapshot().Launches);
        }
    }

    [Theory(Skip = "Requires Windows launch telemetry.", SkipUnless = nameof(IsWindows))]
    [InlineData("ambiguous")]
    [InlineData("packed-object")]
    [InlineData("replacement")]
    [InlineData("missing")]
    public void ResolveGitScalar_ResolverDeclines_ReturnsGitValue(string shape)
    {
        using var fixture = new GitRefFileResolverParityTests.Fixture();
        var revision = "HEAD^{tree}";
        if (shape == "ambiguous")
        {
            fixture.Git("tag", "-a", "fixture-main", "-m", "tag");
            fixture.Git("commit", "--allow-empty", "-m", "branch advances");
            revision = "fixture-main";
            Assert.NotEqual(fixture.Oracle(revision), fixture.Oracle("HEAD"));
        }
        else if (shape == "packed-object") fixture.Git("gc", "--prune=now");
        else if (shape == "replacement")
        {
            var original = fixture.Oracle("HEAD");
            fixture.Git("commit", "--allow-empty", "-m", "replacement");
            fixture.Git("replace", original, fixture.Oracle("HEAD"));
            revision = original + "^{tree}";
        }
        else revision = "missing-branch";
        Assert.False(GitRefFileResolver.TryResolve(fixture.Repo, revision, out _));
        var expected = Oracle(fixture.Repo, "rev-parse", revision);
        Assert.Equal(shape == "missing", expected is null);
        using var capture = LaunchLockTelemetry.BeginCapture();
        Assert.Equal(expected, GoalAcceptanceVerifier.ResolveGitScalar(fixture.Repo, "rev-parse", revision));
        Assert.Equal(1, capture.Snapshot().Launches);
    }

    [Theory(Skip = "Requires Windows launch telemetry.", SkipUnless = nameof(IsWindows))]
    [InlineData("merge-base")]
    [InlineData("verify")]
    [InlineData("option")]
    public void ResolveGitScalar_OtherArgumentShapes_StillLaunchGit(string shape)
    {
        using var fixture = new GitRefFileResolverParityTests.Fixture();
        string[] arguments = shape switch
        {
            "merge-base" => ["merge-base", "HEAD", "fixture-main"],
            "verify" => ["rev-parse", "--verify", "HEAD"],
            _ => ["rev-parse", "--show-prefix"]
        };
        var expected = Oracle(fixture.Repo, arguments);
        Assert.Equal(shape == "option", expected is null);
        using var capture = LaunchLockTelemetry.BeginCapture();
        Assert.Equal(expected, GoalAcceptanceVerifier.ResolveGitScalar(fixture.Repo, arguments));
        Assert.Equal(1, capture.Snapshot().Launches);
    }

    private static string? Oracle(string repo, params string[] arguments)
    {
        var result = GitCli.Run(repo, arguments);
        Assert.True(result.ProcessStarted);
        Assert.False(result.DrainTimedOut);
        var value = result.Output.Trim();
        return result.Succeeded && value.Length >= 7 && value.All(Uri.IsHexDigit) ? value : null;
    }
}
