using Xunit;

// Parallel-safe: class-owned repositories are immutable after setup; counters are AsyncLocal.
public sealed class GitProbeRefFileFastPathParityTests(
    GitProbeRefFileFastPathParityTests.Repositories repositories)
    : IClassFixture<GitProbeRefFileFastPathParityTests.Repositories>
{
    public static IEnumerable<object[]> PlainReads()
    {
        foreach (var shape in new[] { "normal", "linked", "packed" })
        foreach (var revision in new[]
                 { "HEAD", "refs/heads/fixture-main", "fixture-main", "HEAD^{commit}", "HEAD^{tree}" })
        foreach (var flags in new[] { "", "--verify", "-q", "--quiet", "--verify -q", "-q --verify" })
            yield return [shape, revision, flags];
    }

    [Theory]
    [MemberData(nameof(PlainReads))]
    public void PlainRead_MatchesRealGitWithoutLaunching(string shape, string revision, string flags)
    {
        var repo = repositories.Get(shape);
        var arguments = new[] { "rev-parse" }
            .Concat(flags.Split(' ', StringSplitOptions.RemoveEmptyEntries)).Append(revision).ToArray();
        // An explicit empty override independently forces the unchanged real-git path.
        var expected = RunGitProbe(repo, arguments, commandEnvironment: new Dictionary<string, string>());
        Assert.True(expected.Succeeded, expected.ToString());
        using var launches = CountGitProbeLaunches();
        using var answers = CountGitProbeRefFileAnswers();

        var actual = RunGitProbe(repo, arguments);

        Assert.True(actual.Succeeded, actual.ToString());
        Assert.Equal(expected.StandardOutput, actual.StandardOutput);
        Assert.Equal(expected.StandardError, actual.StandardError);
        Assert.Equal(expected.ExitCode, actual.ExitCode);
        Assert.Equal(expected.StandardOutputByteCount, actual.StandardOutputByteCount);
        Assert.Equal(expected.StandardErrorByteCount, actual.StandardErrorByteCount);
        Assert.Equal(expected.StandardOutput.TrimEnd('\n') + "\n", actual.StandardOutput);
        Assert.Contains("resolved from ref files", actual.Command, StringComparison.Ordinal);
        Assert.Contains("resolved=ref-files", actual.EnvironmentContract, StringComparison.Ordinal);
        Assert.Equal(arguments, actual.Arguments);
        Assert.Equal(Path.GetFullPath(repo), actual.RepositoryDirectory);
        Assert.Null(actual.ChildProcessId);
        Assert.Null(actual.ChildStartedAt);
        Assert.Equal(0, launches.Count);
        Assert.Equal(1, answers.Count);
    }

    [Theory]
    [InlineData("--verify --quiet")]
    [InlineData("--quiet --verify")]
    [InlineData("--verify -q --verify --quiet -q")]
    public void RepeatedOrLongQuietFlags_StillAnswerFromFiles(string flags)
    {
        var repo = repositories.Get("normal");
        var arguments = new[] { "rev-parse" }.Concat(flags.Split(' ')).Append("HEAD").ToArray();
        var expected = RunGitProbe(repo, arguments, commandEnvironment: new Dictionary<string, string>());
        Assert.True(expected.Succeeded, expected.ToString());
        using var launches = CountGitProbeLaunches();
        using var answers = CountGitProbeRefFileAnswers();

        var actual = RunGitProbe(repo, arguments);

        Assert.Equal(expected.StandardOutput, actual.StandardOutput);
        Assert.Equal(expected.StandardError, actual.StandardError);
        Assert.Equal(expected.ExitCode, actual.ExitCode);
        Assert.Equal(0, launches.Count);
        Assert.Equal(1, answers.Count);
    }

    [Fact]
    public void AnswerCounters_NestedScopesRestoreOuterAndIgnoreLaunches()
    {
        var repo = repositories.Get("normal");
        using var launches = CountGitProbeLaunches();
        using var outer = CountGitProbeRefFileAnswers();
        Assert.True(RunGitProbe(repo, ["rev-parse", "HEAD"]).Succeeded);
        var inner = CountGitProbeRefFileAnswers();
        try
        {
            Assert.True(RunGitProbe(repo, ["rev-parse", "HEAD"]).Succeeded);
            Assert.Equal(1, inner.Count);
            Assert.Equal(1, outer.Count);
        }
        finally { inner.Dispose(); }
        inner.Dispose(); // Like the launch counter, repeated disposal must be harmless.
        Assert.True(RunGitProbe(repo, ["rev-parse", "HEAD"]).Succeeded);
        Assert.Equal(1, inner.Count);
        Assert.Equal(2, outer.Count);
        Assert.Equal(0, launches.Count);
        Assert.True(RunGitProbe(repo, ["rev-parse", "HEAD"],
            commandEnvironment: new Dictionary<string, string>()).Succeeded);
        Assert.Equal(1, launches.Count);
        Assert.Equal(2, outer.Count);
    }

    public sealed class Repositories : IDisposable
    {
        private readonly GitRefFileResolverParityTests.Fixture normal = new();
        private readonly GitRefFileResolverParityTests.Fixture linked = new();
        private readonly GitRefFileResolverParityTests.Fixture packed = new();
        private readonly string linkedPath;

        public Repositories()
        {
            linkedPath = Path.Combine(linked.Root, "linked");
            linked.Git("worktree", "add", "-b", "other", linkedPath);
            Assert.True(File.Exists(Path.Combine(linkedPath, ".git")));
            packed.Git("pack-refs", "--all");
            Assert.True(File.Exists(Path.Combine(packed.Repo, ".git", "packed-refs")));
            Assert.False(File.Exists(Path.Combine(packed.Repo, ".git", "refs", "heads", "fixture-main")));
            Assert.True(File.Exists(Path.Combine(normal.Repo, ".git", "refs", "heads", "fixture-main")));
        }

        internal string Get(string shape) => shape switch
        {
            "normal" => normal.Repo,
            "linked" => linkedPath,
            "packed" => packed.Repo,
            _ => throw new ArgumentOutOfRangeException(nameof(shape))
        };

        public void Dispose()
        {
            normal.Dispose();
            linked.Dispose();
            packed.Dispose();
        }
    }
}
