using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: private memo instances, unique directories and AsyncLocal launch captures.
public sealed class GitAncestryMemoTests
{
    public static bool IsWindows => OperatingSystem.IsWindows();
    private static string Id(char value, int length = 40) => new(value, length);
    private static string Common() => Path.Combine(Path.GetTempPath(), "ancestry-" + Guid.NewGuid().ToString("N"));

    [Theory(Skip = "Requires Windows launch telemetry.", SkipUnless = nameof(IsWindows))]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void IsAncestor_RepeatedImmutablePair_AvoidsGit(bool reverse, bool sha256)
    {
        using var fixture = new GitRefFileResolverParityTests.Fixture(sha256);
        var first = fixture.Oracle("HEAD");
        fixture.Git("commit", "--allow-empty", "-m", "descendant");
        var second = fixture.Oracle("HEAD");
        var ancestor = reverse ? second : first;
        var descendant = reverse ? first : second;
        var expected = GitCli.RunForRepository(fixture.Repo, "merge-base", "--is-ancestor", ancestor, descendant);
        Assert.Equal(reverse ? 1 : 0, expected.ExitCode);
        var memo = new GitAncestryMemo(4);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var capture = LaunchLockTelemetry.BeginCapture();
            var actual = memo.IsAncestor(Path.Combine(fixture.Repo, ".git"), ancestor, descendant, fixture.Repo);
            Assert.Equal(expected, actual);
            Assert.Equal(attempt == 0 ? 1L : 0L,
                capture.Snapshot().ByEntry[(int)LaunchLockEntryPoint.GitCli]);
        }
        Assert.Equal(1, memo.Hits);
        Assert.Equal(1, memo.Misses);
    }

    [Theory]
    [InlineData("abbreviated", false)]
    [InlineData("abbreviated", true)]
    [InlineData("ref", false)]
    [InlineData("ref", true)]
    [InlineData("uppercase", false)]
    [InlineData("uppercase", true)]
    [InlineData("nonhex", false)]
    [InlineData("nonhex", true)]
    public void IsAncestor_IneligibleOperand_AlwaysRunsGit(string shape, bool descendant)
    {
        var operand = shape switch
        {
            "abbreviated" => Id('a', 12), "ref" => "refs/heads/main",
            "uppercase" => Id('A'), _ => Id('g')
        };
        var ancestorId = descendant ? Id('a') : operand;
        var descendantId = descendant ? operand : Id('b');
        var calls = 0;
        var memo = new GitAncestryMemo(4, (root, args) =>
        {
            calls++;
            Assert.Equal("worktree", root);
            Assert.Equal(new[] { "merge-base", "--is-ancestor", ancestorId, descendantId }, args);
            return new(0, "", "");
        });
        var common = Common();
        for (var i = 0; i < 2; i++)
            Assert.Equal(0, memo.IsAncestor(common, ancestorId, descendantId, "worktree").ExitCode);
        Assert.Equal(2, calls);
        Assert.Equal(0, memo.Hits);
        Assert.Equal(2, memo.Misses);
    }

    [Theory]
    [InlineData(128, false, true, "fatal")]
    [InlineData(-1, false, true, "timeout")]
    [InlineData(0, true, true, "")]
    [InlineData(1, true, true, "")]
    [InlineData(1, false, false, "failed to start git process")]
    [InlineData(0, false, false, "")]
    [InlineData(1, false, true, "exception")]
    public void IsAncestor_IncompleteOrFailedResult_IsReturnedAndRetried(
        int exit, bool drained, bool started, string error)
    {
        var calls = 0;
        var expected = new GitCli.GitResult(exit, "output", error, drained, started);
        var memo = new GitAncestryMemo(4, (_, _) => { calls++; return expected; });
        var common = Common();
        for (var i = 0; i < 2; i++)
            Assert.Equal(expected, memo.IsAncestor(common, Id('a'), Id('b'), "worktree"));
        Assert.Equal(2, calls);
        Assert.Equal(0, memo.Hits);
        Assert.Equal(2, memo.Misses);
    }

    [Fact]
    public void IsAncestor_SeparateRepositoriesAndDirections_KeepSeparateAnswers()
    {
        var first = Common();
        var second = Common();
        var calls = 0;
        var memo = new GitAncestryMemo(4, (root, args) =>
        {
            calls++;
            return new(root == "first" && args[2] == Id('a') ? 0 : 1, "", "");
        });
        for (var i = 0; i < 2; i++)
        {
            Assert.Equal(0, memo.IsAncestor(first, Id('a'), Id('b'), "first").ExitCode);
            Assert.Equal(1, memo.IsAncestor(second, Id('a'), Id('b'), "second").ExitCode);
            Assert.Equal(1, memo.IsAncestor(first, Id('b'), Id('a'), "first").ExitCode);
        }
        Assert.Equal(3, calls);
        Assert.Equal(3, memo.Hits);
    }

    [Fact]
    public void IsAncestor_NormalizedCommonDirectory_SharesAcrossWorktrees()
    {
        var calls = 0;
        var common = Common();
        var memo = new GitAncestryMemo(4, (_, _) => { calls++; return new(0, "", ""); });
        memo.IsAncestor(common, Id('a'), Id('b'), "first-worktree");
        var equivalent = Path.Combine(common, ".") + Path.DirectorySeparatorChar;
        if (OperatingSystem.IsWindows()) equivalent = equivalent.ToUpperInvariant();
        memo.IsAncestor(equivalent, Id('a'), Id('b'), "linked-worktree");
        Assert.Equal(1, calls);
        Assert.Equal(1, memo.Hits);
    }

    [Fact]
    public void IsAncestor_RelativeCommonDirectory_NeverCaches()
    {
        var calls = 0;
        var memo = new GitAncestryMemo(4, (_, _) => { calls++; return new(0, "", ""); });
        for (var i = 0; i < 2; i++) memo.IsAncestor("relative-common", Id('a'), Id('b'), "worktree");
        Assert.Equal(2, calls);
        Assert.Equal(0, memo.Hits);
    }

    [Fact]
    public void IsAncestor_CapacityReached_EvictsLeastRecentlyUsedAnswer()
    {
        var calls = 0;
        var common = Common();
        var memo = new GitAncestryMemo(2, (_, args) =>
        {
            calls++;
            return new(args[2] == Id('b') ? 1 : 0, "", "");
        });
        int Read(char ancestor) => memo.IsAncestor(common, Id(ancestor), Id('f'), "worktree").ExitCode;
        Assert.Equal(0, Read('a'));
        Assert.Equal(1, Read('b'));
        Assert.Equal(0, Read('a')); // Refresh a, so b (including its negative answer) is evicted.
        Assert.Equal(0, Read('c'));
        Assert.Equal(0, Read('a'));
        Assert.Equal(3, calls);
        Assert.Equal(1, Read('b'));
        Assert.Equal(4, calls);
    }

    [Fact]
    public void IsAncestor_ConcurrentRequests_KeepCompletedEntriesConsistent()
    {
        var calls = 0;
        var common = Common();
        var memo = new GitAncestryMemo(16, (_, _) =>
        {
            Interlocked.Increment(ref calls);
            return new(0, "", "");
        });
        Parallel.For(0, 128, i =>
            Assert.Equal(0, memo.IsAncestor(common, Id((char)('a' + i % 6)), Id('f'), "worktree").ExitCode));
        var before = calls;
        Assert.InRange(before, 6, 128); // Concurrent misses need not share a git invocation.
        for (var i = 0; i < 6; i++)
            Assert.Equal(0, memo.IsAncestor(common, Id((char)('a' + i)), Id('f'), "worktree").ExitCode);
        Assert.Equal(before, calls);
        Assert.Equal(134, memo.Hits + memo.Misses);
    }

    [Theory]
    [InlineData("shallow")]
    [InlineData("info/grafts")]
    [InlineData("refs/replace")]
    public void IsAncestor_MutableHistoryMetadata_BypassesExistingAnswer(string marker)
    {
        var root = Directory.CreateTempSubdirectory("ancestry-history-").FullName;
        try
        {
            var calls = 0;
            var memo = new GitAncestryMemo(4, (_, _) => { calls++; return new(0, "", ""); });
            memo.IsAncestor(root, Id('a'), Id('b'), "worktree");
            var path = Path.Combine(root, marker);
            if (marker == "refs/replace") Directory.CreateDirectory(path);
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, "history changes");
            }
            for (var i = 0; i < 2; i++) memo.IsAncestor(root, Id('a'), Id('b'), "worktree");
            Assert.Equal(3, calls);
            Assert.Equal(0, memo.Hits);
        }
        finally { SharedTestSupport.RemoveTempDirectory(root); }
    }
}
