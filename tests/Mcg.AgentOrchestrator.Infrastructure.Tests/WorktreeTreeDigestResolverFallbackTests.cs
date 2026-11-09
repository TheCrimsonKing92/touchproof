using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: explicit environment input, unique repositories, no global environment mutations.
public sealed class WorktreeTreeDigestResolverFallbackTests
{
    [Theory]
    [InlineData("environment")]
    [InlineData("refs-replace")]
    public void TryCompute_ResolverDeclines_EqualsOriginalThroughFallback(string shape)
    {
        using var fixture = new WorktreeTreeDigestFixture();
        Assert.True(GitRefFileResolver.TryResolve(fixture.Repo, "HEAD^{tree}", out _, fixture.ReadEnvironment));
        Func<string, string?> environment = fixture.ReadEnvironment;
        if (shape == "environment")
            environment = name => name == "GIT_NO_REPLACE_OBJECTS" ? "1" : fixture.ReadEnvironment(name);
        else
        {
            var original = fixture.Git("rev-parse", "HEAD").Trim();
            fixture.Git("commit", "--allow-empty", "-m", "replacement");
            var replacement = fixture.Git("rev-parse", "HEAD").Trim();
            Assert.NotEqual(original, replacement);
            fixture.Git("checkout", "--detach", original);
            fixture.Git("replace", original, replacement);
            Assert.True(Directory.Exists(Path.Combine(fixture.Repo, ".git", "refs", "replace")));
        }
        Assert.Empty(fixture.Git("status", "--porcelain=v2", "-z", "--untracked-files=all", "--ignore-submodules=none"));
        Assert.True(GitRevisionReader.TryLocateRefStore(fixture.Repo, environment, out _, out _));
        Assert.False(GitRefFileResolver.TryResolve(fixture.Repo, "HEAD^{tree}", out _, environment));
        var expected = fixture.OriginalDigest();
        Assert.Equal(expected, fixture.Compute(environment));
        Assert.Equal(["status", "read-tree", "add", "write-tree"], fixture.Launches.Select(launch => launch.Arguments[0]));
    }
}
