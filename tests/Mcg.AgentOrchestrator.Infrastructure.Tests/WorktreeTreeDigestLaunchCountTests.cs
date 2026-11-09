using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: AsyncLocal telemetry and a synchronous thread-local observer, with unique repositories.
public sealed class WorktreeTreeDigestLaunchCountTests
{
    public static bool IsWindows => OperatingSystem.IsWindows();

    [Theory(Skip = "Requires Windows launch telemetry.", SkipUnless = nameof(IsWindows))]
    [InlineData("clean")]
    [InlineData("dirty")]
    [InlineData("discovery-decline")]
    public void TryCompute_WorktreeState_UsesOnlyRequiredGitLaunches(string shape)
    {
        using var fixture = new WorktreeTreeDigestFixture();
        Assert.True(GitRefFileResolver.TryResolve(fixture.Repo, "HEAD^{tree}", out _, fixture.ReadEnvironment));
        if (shape == "dirty") File.WriteAllText(Path.Combine(fixture.Repo, "tracked.txt"), "dirty\n");
        if (shape == "discovery-decline")
        {
            File.AppendAllText(Path.Combine(fixture.Repo, ".git", "config"), "\n[gc]\nreflogExpire = never\n");
            Assert.False(GitRevisionReader.TryLocateRefStore(fixture.Repo, fixture.ReadEnvironment, out _, out _));
        }
        // Exercise the public-to-the-assembly convenience overload as well as the detailed overload.
        if (shape == "clean")
            Assert.True(GitRevisionReader.TryLocateRefStore(fixture.Repo, Environment.GetEnvironmentVariable, out _, out _));
        using var capture = LaunchLockTelemetry.BeginCapture();
        fixture.Compute(defaultOverload: shape == "clean");
        var snapshot = capture.Snapshot();
        var expected = shape switch
        {
            "clean" => new[] { "status" },
            "dirty" => new[] { "status", "read-tree", "add", "write-tree" },
            _ => new[] { "rev-parse", "read-tree", "add", "write-tree" }
        };
        Assert.Equal(expected, fixture.Launches.Select(launch => launch.Arguments[0]));
        Assert.Equal((long)expected.Length, snapshot.Launches);
        Assert.Equal((long)expected.Length, snapshot.ByEntry[(int)LaunchLockEntryPoint.Start]);
        foreach (var launch in fixture.Launches.Where(launch => launch.Arguments[0] == "status"))
        {
            Assert.Equal(["status", "--porcelain=v2", "-z", "--untracked-files=all", "--ignore-submodules=none"], launch.Arguments);
            Assert.Equal("0", launch.OptionalLocks);
        }
    }
}
