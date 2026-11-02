using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

// Each test owns an isolated repository and worktree; no shared repository configuration is changed.
public sealed class WorkerGitContextMergeTreeMemoTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Read_SameCommitPair_ReusesCleanOrConflictedStatus(bool conflicted)
    {
        using var fixture = conflicted ? new AdditiveConflictGitFixture() : CleanFixture();
        var launches = new MergeTreeCounter();
        var context = Context(launches);

        var first = context.ReadReviewerMergeTreeStatus(fixture.Worktree, "main");
        var second = context.ReadReviewerMergeTreeStatus(
            fixture.Worktree, fixture.MainHead, fixture.OriginalHead);

        Assert.Equal(first, second);
        Assert.Equal(!conflicted, first.IsClean);
        Assert.Equal(conflicted ? [AdditiveConflictGitFixture.WidgetPath] : Array.Empty<string>(),
            first.ConflictPaths);
        Assert.Equal(1, launches.Count);
        Assert.Equal(new[] { fixture.MainHead, fixture.OriginalHead }, launches.LastArguments![3..]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Read_BranchAdvances_RecomputesConflict(bool advanceMain)
    {
        using var fixture = CleanFixture();
        var launches = new MergeTreeCounter();
        var context = Context(launches);
        Assert.True(context.ReadReviewerMergeTreeStatus(fixture.Worktree, "main").IsClean);
        Assert.Equal(1, launches.Count);

        var root = advanceMain ? fixture.Repository : fixture.Worktree;
        var path = advanceMain ? "src/GoalOnly.cs" : "src/MainOnly.cs";
        File.WriteAllText(Path.Combine(root, path), "opposing change\n");
        AdditiveConflictGitFixture.Commit(root, "Introduce conflict");

        var changed = context.ReadReviewerMergeTreeStatus(fixture.Worktree, "main");
        Assert.False(changed.IsClean);
        Assert.Equal(new[] { path }, changed.ConflictPaths);
        Assert.Equal(2, launches.Count);
        Assert.Equal(changed, context.ReadReviewerMergeTreeStatus(fixture.Worktree, "main"));
        Assert.Equal(2, launches.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Read_UnresolvedReference_BypassesMemo(bool unresolvedHead)
    {
        using var fixture = CleanFixture();
        var launches = new MergeTreeCounter();
        var context = Context(launches);
        var baseRef = unresolvedHead ? "main" : "refs/heads/missing";
        var headRef = unresolvedHead ? "refs/heads/missing" : "HEAD";

        for (var i = 0; i < 2; i++)
            Assert.Throws<ReviewerMergeTreeStatusException>(() =>
                context.ReadReviewerMergeTreeStatus(fixture.Worktree, baseRef, headRef));

        Assert.Equal(2, launches.Count);
        Assert.Equal(new[] { baseRef, headRef }, launches.LastArguments![3..]);
    }

    [Fact]
    public void Read_UnresolvedButSuccessfulCall_DoesNotRememberResult()
    {
        using var fixture = CleanFixture();
        var launches = new MergeTreeCounter();
        var context = new WorkerGitContext(launches.Run, (_, _) => null);

        Assert.True(context.ReadReviewerMergeTreeStatus(fixture.Worktree, "main").IsClean);
        Assert.True(context.ReadReviewerMergeTreeStatus(fixture.Worktree, "main").IsClean);
        Assert.Equal(2, launches.Count);
        Assert.Equal(new[] { "main", "HEAD" }, launches.LastArguments![3..]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Read_FailedCall_RetriesThenRemembersSuccess(bool throwFromRunner)
    {
        using var fixture = CleanFixture();
        var launches = new MergeTreeCounter();
        var attempts = 0;
        var context = new WorkerGitContext((directory, timeout, args) =>
        {
            if (++attempts == 1)
            {
                if (throwFromRunner)
                    throw new ReviewerMergeTreeStatusException(
                        WorkerGitContext.ReviewerMergeTreeUnavailableErrorCode, "Injected failure");
                return new GitCli.GitResult(2, "", "Injected failure");
            }
            return launches.Run(directory, timeout, args);
        }, ResolveCommit);

        Assert.Throws<ReviewerMergeTreeStatusException>(() =>
            context.ReadReviewerMergeTreeStatus(fixture.Worktree, "main"));
        var success = context.ReadReviewerMergeTreeStatus(fixture.Worktree, "main");
        Assert.True(success.IsClean);
        Assert.Equal(success, context.ReadReviewerMergeTreeStatus(fixture.Worktree, "main"));
        Assert.Equal(2, attempts);
        Assert.Equal(1, launches.Count);
    }

    [Fact]
    public void Read_SwappedPair_UsesSeparateKey()
    {
        using var fixture = CleanFixture();
        var launches = new MergeTreeCounter();
        var context = Context(launches);
        Assert.True(context.ReadReviewerMergeTreeStatus(
            fixture.Worktree, fixture.MainHead, fixture.OriginalHead).IsClean);
        Assert.True(context.ReadReviewerMergeTreeStatus(
            fixture.Worktree, fixture.OriginalHead, fixture.MainHead).IsClean);
        Assert.Equal(2, launches.Count);
        context.ReadReviewerMergeTreeStatus(fixture.Worktree, fixture.MainHead, fixture.OriginalHead);
        Assert.Equal(2, launches.Count);
    }

    [Fact]
    public void Read_SeparateInstances_DoNotShareResults()
    {
        using var fixture = CleanFixture();
        var launches = new MergeTreeCounter();
        var first = Context(launches);
        var second = Context(launches);
        Assert.True(first.ReadReviewerMergeTreeStatus(fixture.Worktree, "main").IsClean);
        Assert.True(second.ReadReviewerMergeTreeStatus(fixture.Worktree, "main").IsClean);
        Assert.Equal(2, launches.Count);
    }

    [Fact]
    public void Read_CapacityExceeded_EvictsFirstInsertionDespiteRecentHit()
    {
        using var fixture = CleanFixture();
        var runs = 0;
        // Synthetic commit ids isolate FIFO policy from the cost of creating 257 commits.
        var context = new WorkerGitContext((_, _, _) =>
        {
            runs++;
            return new GitCli.GitResult(0, new string('a', 40), "");
        }, (_, revision) => int.Parse(revision).ToString("x40"));

        for (var i = 0; i < 256; i++)
            context.ReadReviewerMergeTreeStatus(fixture.Worktree, "1000", i.ToString());
        context.ReadReviewerMergeTreeStatus(fixture.Worktree, "1000", "0");
        Assert.Equal(256, runs);
        context.ReadReviewerMergeTreeStatus(fixture.Worktree, "1000", "256");
        Assert.Equal(257, runs);
        context.ReadReviewerMergeTreeStatus(fixture.Worktree, "1000", "0");
        Assert.Equal(258, runs);
        context.ReadReviewerMergeTreeStatus(fixture.Worktree, "1000", "256");
        Assert.Equal(258, runs);
    }

    [Fact]
    public void Read_ConcurrentMisses_MapRemainsUsableAndBounded()
    {
        using var fixture = CleanFixture();
        var runs = 0;
        var context = new WorkerGitContext((_, _, _) =>
        {
            Interlocked.Increment(ref runs);
            return new GitCli.GitResult(0, new string('a', 40), "");
        }, (_, revision) => int.Parse(revision).ToString("x40"));

        Parallel.For(0, 512, i => Assert.True(context.ReadReviewerMergeTreeStatus(
            fixture.Worktree, "1000", (i % 300).ToString()).IsClean));
        var before = runs;
        // At most 256 of these 300 previously queried keys can remain remembered.
        for (var i = 0; i < 300; i++)
            context.ReadReviewerMergeTreeStatus(fixture.Worktree, "1000", i.ToString());
        Assert.True(runs >= before + 44);
        before = runs;
        context.ReadReviewerMergeTreeStatus(fixture.Worktree, "1000", "299");
        Assert.Equal(before, runs);
    }

    [Fact]
    public void CreateForRepository_UnchangedPair_ProjectsTwiceWithOneLaunch()
    {
        using var fixture = CleanFixture();
        var launches = new MergeTreeCounter();
        var projector = GateReadyCandidateProjector.CreateForRepository(
            fixture.Repository, "main", reviewerMergeTreeGitRunner: launches.Run);
        var input = new GateReadyCandidateInput(fixture.GoalId, GoalLifecycleState.Verified,
            VerificationGateSatisfied: true, ChangeRiskTier.DocsOnly, ConductorTransitionDecision.Auto);

        Assert.IsType<GateReadyCandidateProjectionResult.Ready>(projector.Project(input));
        Assert.IsType<GateReadyCandidateProjectionResult.Ready>(projector.Project(input));
        Assert.Equal(1, launches.Count);
    }

    private static WorkerGitContext Context(MergeTreeCounter launches) =>
        new(launches.Run, ResolveCommit);

    private static string? ResolveCommit(string directory, string revision) =>
        GitRevisionReader.Shared.TryResolve(directory, revision, out var id) ? id : null;

    private static AdditiveConflictGitFixture CleanFixture() => new(
    [
        new("src/MainOnly.cs", "base\n", "main\n", "base\n"),
        new("src/GoalOnly.cs", "base\n", "base\n", "goal\n")
    ]);

    private sealed class MergeTreeCounter
    {
        internal int Count { get; private set; }
        internal string[]? LastArguments { get; private set; }

        internal GitCli.GitResult Run(string directory, int timeout, string[] arguments)
        {
            Assert.Equal("merge-tree", arguments[0]);
            Count++;
            LastArguments = arguments;
            return GitCli.Run(directory, timeout, arguments);
        }
    }
}
