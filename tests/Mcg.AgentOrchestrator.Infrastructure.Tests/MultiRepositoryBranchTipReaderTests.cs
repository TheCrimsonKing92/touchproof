using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;
using Fixture = GitRefFileResolverParityTests.Fixture;

// Parallel-safe: fixture-owned repositories, child-only environment and test-scoped census windows.
public sealed class MultiRepositoryBranchTipReaderTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Read_ConclusiveBranchRef_MatchesGitWithoutLaunch(bool packed)
    {
        using var fixture = new Fixture();
        const string branch = "fixture-main";
        const string reference = "refs/heads/" + branch;
        if (packed) fixture.Git("pack-refs", "--all");
        var loosePath = Path.Combine(fixture.Repo, ".git", "refs", "heads", branch);
        Assert.Equal(!packed, File.Exists(loosePath));
        if (packed)
            Assert.Contains(" " + reference, File.ReadAllText(Path.Combine(fixture.Repo, ".git", "packed-refs")));
        var expected = RepositoryIntegrationLanding.Git(fixture.Repo, "rev-parse", reference);

        var window = Observe();
        var actual = MultiRepositoryBranchTipReader.Read(fixture.Repo, branch,
            () => throw new InvalidOperationException("A conclusive branch ref must not invoke git."));
        AssertCommands(window);
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Read_MissingBranch_PreservesGitException(bool verify)
    {
        using var fixture = new Fixture();
        string[] arguments = verify
            ? ["rev-parse", "--verify", "refs/heads/missing"]
            : ["rev-parse", "refs/heads/missing"];
        var expected = Assert.Throws<InvalidOperationException>(
            () => RepositoryIntegrationLanding.Git(fixture.Repo, arguments));

        var window = Observe();
        var actual = Assert.Throws<InvalidOperationException>(() =>
            MultiRepositoryBranchTipReader.Read(fixture.Repo, "missing",
                () => RepositoryIntegrationLanding.Git(fixture.Repo, arguments)));
        AssertCommands(window, "rev-parse");
        Assert.Equal(expected.Message, actual.Message);
    }

    [Theory]
    [InlineData("namespace")]
    [InlineData("lock")]
    public void Read_InconclusiveBranchRef_ReturnsGitWithOneLaunch(string reason)
    {
        using var fixture = new Fixture();
        const string branch = "fixture-main";
        var environment = new Dictionary<string, string>();
        if (reason == "namespace") environment["GIT_NAMESPACE"] = "branch-tip-test";
        else File.WriteAllText(Path.Combine(fixture.Repo, ".git", "refs", "heads", branch + ".lock"), "");
        string? ReadEnvironment(string name) => environment.TryGetValue(name, out var value)
            ? value : Environment.GetEnvironmentVariable(name);
        using var runner = LandingExecutor.UseGitRunner((root, args) =>
            GitCli.RunWithEnvironment(root, environment, args));
        Assert.False(GitRefFileResolver.TryResolve(fixture.Repo, "refs/heads/" + branch, out _, ReadEnvironment));
        var expected = RepositoryIntegrationLanding.Git(fixture.Repo, "rev-parse", "refs/heads/" + branch);

        var window = Observe();
        var actual = MultiRepositoryBranchTipReader.Read(fixture.Repo, branch,
            () => RepositoryIntegrationLanding.Git(fixture.Repo, "rev-parse", "refs/heads/" + branch), ReadEnvironment);
        AssertCommands(window, "rev-parse");
        Assert.Equal(expected, actual);
    }

    private static HostCapacitySlotGitCensus.Window Observe()
    {
        // GitCli.LaunchObserver feeds the census, filtered to this test's identity.
        var identity = HostCapacitySlotTimingWriter.CurrentTestName();
        Assert.NotEqual("<unknown>", identity);
        HostCapacitySlotGitCensus.EnsureInstalled(nameof(MultiRepositoryBranchTipReaderTests));
        return Assert.IsType<HostCapacitySlotGitCensus.Window>(HostCapacitySlotGitCensus.Shared.Open(identity));
    }

    private static void AssertCommands(HostCapacitySlotGitCensus.Window window, params string[] commands)
    {
        var delta = Assert.IsType<HostCapacitySlotGitCensus.GitLaunchDelta>(window.Close());
        Assert.Equal(commands.Length, delta.Launches);
        Assert.Equal(commands.GroupBy(command => command).ToDictionary(group => group.Key, group => group.Count())
            .OrderBy(pair => pair.Key, StringComparer.Ordinal), delta.Subcommands.OrderBy(pair => pair.Key, StringComparer.Ordinal));
    }
}
