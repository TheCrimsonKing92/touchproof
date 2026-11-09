using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: local delegates only; registration and process environment are never changed.
public sealed class WorktreeCleanTreeFastPathTests
{
    [Theory]
    [InlineData("entry")]
    [InlineData("whitespace")]
    [InlineData("failed")]
    [InlineData("wait-timeout")]
    [InlineData("drain-timeout")]
    [InlineData("exception")]
    public void TryMatchClean_InconclusiveStatus_DoesNotResolveOrMatch(string shape)
    {
        var runs = 0;
        var resolutions = 0;
        string? observedRoot = null;
        Dictionary<string, string>? observedEnvironment = null;
        string[]? observedArguments = null;
        Assert.False(WorktreeCleanTreeFastPath.TryMatchClean("repo", _ => null, (root, environment, args) =>
        {
            runs++;
            observedRoot = root;
            observedEnvironment = environment;
            observedArguments = args;
            return shape switch
            {
                "entry" => new(0, "? new.txt\0", null, false),
                "whitespace" => new(0, " \n", null, false),
                "failed" => new(128, "", "fatal", false),
                "wait-timeout" => new(0, "", null, true, "wait"),
                "drain-timeout" => new(0, "", null, true, "drain"),
                _ => throw new IOException("status unavailable")
            };
        }, (_, _) => { resolutions++; return new string('a', 40); }, out var tree));
        Assert.Equal(1, runs);
        Assert.Equal(0, resolutions);
        Assert.Equal("repo", observedRoot);
        Assert.NotNull(observedEnvironment);
        Assert.Equal("0", observedEnvironment["GIT_OPTIONAL_LOCKS"]);
        Assert.Equal(["status", "--porcelain=v2", "-z", "--untracked-files=all", "--ignore-submodules=none"], observedArguments);
        Assert.Empty(tree);
    }

    [Theory]
    [InlineData("declined")]
    [InlineData("invalid-hash")]
    [InlineData("exception")]
    [InlineData("sha1")]
    [InlineData("sha256")]
    public void TryMatchClean_EmptyStatus_RequiresPositiveTreeEvidence(string shape)
    {
        var runs = 0;
        var resolutions = 0;
        string? observedRoot = null;
        Func<string, string?>? observedEnvironment = null;
        var expected = shape == "sha256" ? new string('B', 64) : new string('A', 40);
        Func<string, string?> environment = _ => "sentinel";
        var matched = WorktreeCleanTreeFastPath.TryMatchClean("repo", environment, (_, _, _) =>
        {
            runs++;
            return new(0, "", null, false);
        }, (root, actualEnvironment) =>
        {
            resolutions++;
            observedRoot = root;
            observedEnvironment = actualEnvironment;
            return shape switch
            {
                "declined" => null,
                "invalid-hash" => new string('z', 40),
                "exception" => throw new IOException("resolver unavailable"),
                _ => expected
            };
        }, out var tree);
        Assert.Equal(1, runs);
        Assert.Equal(1, resolutions);
        Assert.Equal("repo", observedRoot);
        Assert.Same(environment, observedEnvironment);
        Assert.Equal(shape is "sha1" or "sha256", matched);
        Assert.Equal(matched ? expected : "", tree);
    }

    [Fact]
    public void TryMatchClean_MissingResolver_DeclinesWithoutLaunch()
    {
        var runs = 0;
        Assert.False(WorktreeCleanTreeFastPath.TryMatchClean("repo", _ => null, (_, _, _) =>
        {
            runs++;
            return new(0, "", null, false);
        }, null, out var tree));
        Assert.Equal(0, runs);
        Assert.Empty(tree);
    }
}
