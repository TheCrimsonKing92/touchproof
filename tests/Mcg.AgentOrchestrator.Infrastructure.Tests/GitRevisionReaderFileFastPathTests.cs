using Mcg.AgentOrchestrator.Infrastructure;
using Fixture = GitRefFileResolverParityTests.Fixture;

// Parallel-safe: fixture-owned repositories/readers and test-identity-scoped census windows.
public sealed class GitRevisionReaderFileFastPathTests
{
    [Theory]
    [InlineData("HEAD")]
    [InlineData("refs/heads/fixture-main")]
    public void ColdRef_ResolvesWithoutGitLaunch(string revision)
    {
        using var fixture = new Fixture();
        using var reader = new GitRevisionReader();
        var expected = fixture.Oracle(revision);
        var window = Observe();
        Assert.True(reader.TryResolve(fixture.Repo, revision, out var actual));
        AssertCommands(window);
        Assert.Equal(expected, actual);
        Assert.Null(reader.GetReader(fixture.Repo));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void MissingBranch_ConfirmsOnlyWhenAbsenceIsUnproven(bool provable)
    {
        using var fixture = new Fixture();
        using var reader = new GitRevisionReader();
        if (!provable) File.AppendAllText(Path.Combine(fixture.Repo, ".git", "config"), "\n[user]\nname = Reader\n");
        Assert.Equal(provable, GitRevisionReader.TryConfirmBranchAbsent(fixture.Repo, "absent"));
        var window = Observe();
        Assert.False(reader.TryResolve(fixture.Repo, "refs/heads/absent", out var actual));
        AssertCommands(window, provable ? ["cat-file"] : ["cat-file", "rev-parse"]);
        Assert.Empty(actual);
        Assert.NotNull(reader.GetReader(fixture.Repo));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ObjectExpressions_KeepBatchAndAbbreviationConfirmation(bool abbreviated)
    {
        using var fixture = new Fixture();
        using var reader = new GitRevisionReader();
        var head = fixture.Oracle("HEAD");
        var revision = abbreviated ? head[..12] : head + "^{tree}";
        var expected = fixture.Oracle(revision);
        Assert.False(GitRefFileResolver.TryResolve(fixture.Repo, revision, out _));
        var window = Observe();
        Assert.True(reader.TryResolve(fixture.Repo, revision, out var actual));
        AssertCommands(window, abbreviated ? ["cat-file", "rev-parse"] : ["cat-file"]);
        Assert.Equal(expected, actual);
        Assert.NotNull(reader.GetReader(fixture.Repo));
    }

    [Fact]
    public void BranchCreatedAfterReaderStart_UsesFreshFileEvidence()
    {
        using var fixture = new Fixture();
        using var reader = new GitRevisionReader();
        Assert.True(reader.TryResolve(fixture.Repo, "HEAD^{commit}", out _));
        var process = reader.GetReader(fixture.Repo);
        fixture.Git("branch", "late");
        var expected = fixture.Oracle("refs/heads/late");
        var window = Observe();
        Assert.True(reader.TryResolve(fixture.Repo, "refs/heads/late", out var actual));
        AssertCommands(window);
        Assert.Equal(expected, actual);
        Assert.Same(process, reader.GetReader(fixture.Repo));
    }

    private static HostCapacitySlotGitCensus.Window Observe()
    {
        // GitCli.LaunchObserver feeds this census; ReaderProcess.Start reports cat-file there too.
        var identity = HostCapacitySlotTimingWriter.CurrentTestName();
        Assert.NotEqual("<unknown>", identity);
        HostCapacitySlotGitCensus.EnsureInstalled(nameof(GitRevisionReaderFileFastPathTests));
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
