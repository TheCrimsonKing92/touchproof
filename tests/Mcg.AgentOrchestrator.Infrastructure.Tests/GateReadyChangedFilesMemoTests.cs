using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each test owns its repository, worktree, counters and injected delegates.
public sealed class GateReadyChangedFilesMemoTests
{
    private const string Workers = "src/Mcg.AgentOrchestrator.Execution/Workers/";

    [Fact]
    public void Project_UnchangedPair_ReturnsEqualFilesWithOneDiff()
    {
        using var fixture = CleanFixture();
        var counter = new DiffCounter();
        var projector = Projector(fixture, counter);
        var expected = Uncached(fixture);
        Assert.NotEmpty(expected.Files);

        var first = Ready(projector, fixture);
        var second = Ready(projector, fixture);

        Assert.Equal(expected.Files, first.LandingPaths);
        Assert.Equal(first, second);
        Assert.Equal(1, counter.Count);
    }

    [Fact]
    public void Project_GoalAdvances_RecomputesFiles()
    {
        using var fixture = CleanFixture();
        var counter = new DiffCounter();
        var projector = Projector(fixture, counter);
        Assert.DoesNotContain(Workers + "Added.cs", Ready(projector, fixture).LandingPaths);
        Assert.Equal(1, counter.Count);
        File.WriteAllText(Path.Combine(fixture.Worktree, Workers + "Added.cs"), "new goal file\n");
        AdditiveConflictGitFixture.Commit(fixture.Worktree, "Advance goal");

        var changed = Ready(projector, fixture);

        Assert.Contains(Workers + "Added.cs", changed.LandingPaths);
        Assert.Equal(Uncached(fixture).Files, changed.LandingPaths);
        Assert.Equal(2, counter.Count);
        Assert.Equal(changed, Ready(projector, fixture));
        Assert.Equal(2, counter.Count);
    }

    [Fact]
    public void Project_MainMergesGoal_RecomputesEmptyScope()
    {
        using var fixture = CleanFixture();
        var counter = new DiffCounter();
        var projector = Projector(fixture, counter);
        Assert.NotEmpty(Ready(projector, fixture).LandingPaths);
        Assert.Equal(1, counter.Count);
        // Move the merge base too: a main-only commit leaves a three-dot diff unchanged.
        AdditiveConflictGitFixture.Git(fixture.Repository, "merge", "--no-edit",
            GoalWorktrees.BranchName(fixture.GoalId));
        Assert.NotEqual(fixture.MainHead, ResolveCommit(fixture.Repository, "HEAD"));

        var changed = Assert.IsType<GateReadyCandidateProjectionResult.Excluded>(
            projector.Project(Input(fixture)));

        Assert.Equal(GateReadyCandidateExclusionReason.ScopeEmpty, changed.Reason);
        Assert.Empty(Uncached(fixture).Files);
        Assert.Equal(2, counter.Count);
        Assert.Equal(changed, projector.Project(Input(fixture)));
        Assert.Equal(2, counter.Count);
    }

    [Fact]
    public void Resolve_MissingGoalBranch_RetriesExistingPathUntilBranchExists()
    {
        using var fixture = CleanFixture();
        var missingGoal = GoalId.New();
        var calls = 0;
        var memo = new GateReadyChangedFilesMemo(ResolveCommit, goalId =>
        {
            calls++;
            return GoalWorktrees.ResolveChangedFilesAgainstHead(fixture.Repository, goalId);
        });

        for (var i = 0; i < 2; i++)
        {
            var actual = memo.Resolve(fixture.Repository, missingGoal);
            var expected = GoalWorktrees.ResolveChangedFilesAgainstHead(fixture.Repository, missingGoal);
            Assert.False(actual.Succeeded);
            Assert.Equal(expected.FailureReason, actual.FailureReason);
            Assert.Equal(expected.Files, actual.Files);
        }
        Assert.Equal(2, calls);

        AdditiveConflictGitFixture.Git(fixture.Repository, "branch",
            GoalWorktrees.BranchName(missingGoal), GoalWorktrees.BranchName(fixture.GoalId));
        Assert.True(memo.Resolve(fixture.Repository, missingGoal).Succeeded);
        Assert.True(memo.Resolve(fixture.Repository, missingGoal).Succeeded);
        Assert.Equal(3, calls);
    }

    [Theory]
    [InlineData("HEAD")]
    [InlineData("goal")]
    public void Project_UnresolvedButSuccessfulDiff_DoesNotRemember(string unresolved)
    {
        using var fixture = CleanFixture();
        var counter = new DiffCounter();
        var projector = GateReadyCandidateProjector.CreateForRepository(fixture.Repository, "main",
            changedFilesGitRunner: counter.Run,
            changedFilesCommitResolver: (directory, revision) =>
                (unresolved == "HEAD" ? revision == "HEAD" : revision.StartsWith("refs/heads/goal/"))
                    ? null : ResolveCommit(directory, revision));

        Assert.Equal(Uncached(fixture).Files, Ready(projector, fixture).LandingPaths);
        Assert.Equal(Uncached(fixture).Files, Ready(projector, fixture).LandingPaths);
        Assert.Equal(2, counter.Count);
    }

    [Theory]
    [InlineData("exit")]
    [InlineData("drain")]
    [InlineData("throw")]
    public void Project_FailedDiff_RetriesThenRemembersSuccess(string failure)
    {
        using var fixture = CleanFixture();
        var counter = new DiffCounter();
        var attempts = 0;
        var projector = GateReadyCandidateProjector.CreateForRepository(fixture.Repository, "main",
            changedFilesGitRunner: (directory, arguments) =>
            {
                if (++attempts != 1) return counter.Run(directory, arguments);
                if (failure == "throw") throw new InvalidOperationException("Injected diff failure");
                return failure == "drain"
                    ? new GitCli.GitResult(0, "", "") { DrainTimedOut = true }
                    : new GitCli.GitResult(1, "", "Injected diff failure");
            });

        var failed = Assert.IsType<GateReadyCandidateProjectionResult.Excluded>(
            projector.Project(Input(fixture)));
        Assert.Equal(GateReadyCandidateExclusionReason.ScopeResolutionFailed, failed.Reason);
        Assert.Equal(Uncached(fixture).Files, Ready(projector, fixture).LandingPaths);
        Assert.Equal(Uncached(fixture).Files, Ready(projector, fixture).LandingPaths);
        Assert.Equal(2, attempts);
        Assert.Equal(1, counter.Count);
    }

    [Fact]
    public void Project_TwoInstances_EachComputesItsOwnDiff()
    {
        using var fixture = CleanFixture();
        var counter = new DiffCounter();
        var first = Projector(fixture, counter);
        var second = Projector(fixture, counter);

        Assert.Equal(Ready(first, fixture), Ready(second, fixture));
        Assert.Equal(Ready(first, fixture), Ready(second, fixture));
        Assert.Equal(2, counter.Count);
    }

    [Fact]
    public void Resolve_CapacityExceeded_EvictsOldestDespiteRecentHit()
    {
        using var fixture = CleanFixture();
        var key = 0;
        var calls = 0;
        // Synthetic resolved ids isolate capacity from the cost of creating 257 commits.
        var memo = new GateReadyChangedFilesMemo(
            (_, revision) => revision == "HEAD" ? fixture.MainHead : key.ToString("x40"),
            _ => new GoalWorktreeChangedFilesResult(true, [$"file-{++calls}.cs"], null));

        for (key = 0; key < 256; key++) memo.Resolve(fixture.Repository, fixture.GoalId);
        key = 0;
        Assert.Equal(new[] { "file-1.cs" }, memo.Resolve(fixture.Repository, fixture.GoalId).Files);
        Assert.Equal(256, calls);
        key = 256;
        memo.Resolve(fixture.Repository, fixture.GoalId);
        Assert.Equal(257, calls);
        key = 0;
        Assert.Equal(new[] { "file-258.cs" }, memo.Resolve(fixture.Repository, fixture.GoalId).Files);
        Assert.Equal(258, calls);
        key = 256;
        Assert.Equal(new[] { "file-257.cs" }, memo.Resolve(fixture.Repository, fixture.GoalId).Files);
        Assert.Equal(258, calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Resolve_RefsMoveDuringDiff_ReturnsResultWithoutRemembering(bool moveMain)
    {
        using var fixture = CleanFixture();
        var counter = new DiffCounter();
        var moved = false;
        var memo = new GateReadyChangedFilesMemo(ResolveCommit, goalId =>
        {
            if (!moved)
            {
                moved = true;
                var root = moveMain ? fixture.Repository : fixture.Worktree;
                File.WriteAllText(Path.Combine(root, Workers + "Moved.cs"), "moved\n");
                AdditiveConflictGitFixture.Commit(root, "Move during diff");
            }
            return GoalWorktrees.ResolveChangedFilesAgainstHead(fixture.Repository, goalId, counter.Run);
        });

        var observed = memo.Resolve(fixture.Repository, fixture.GoalId);
        Assert.True(observed.Succeeded);
        Assert.Equal(Uncached(fixture).Files, observed.Files);
        Assert.Equal(1, counter.Count);
        // Restore the old pair. A result saved under it would now contain the wrong observation.
        var reference = moveMain ? "refs/heads/main" : "refs/heads/" + GoalWorktrees.BranchName(fixture.GoalId);
        AdditiveConflictGitFixture.Git(fixture.Repository, "update-ref", reference,
            moveMain ? fixture.MainHead : fixture.OriginalHead);
        Assert.Equal(Uncached(fixture).Files, memo.Resolve(fixture.Repository, fixture.GoalId).Files);
        Assert.Equal(2, counter.Count);
        Assert.Equal(Uncached(fixture).Files, memo.Resolve(fixture.Repository, fixture.GoalId).Files);
        Assert.Equal(2, counter.Count);
    }

    [Fact]
    public void Resolve_SuccessfulFiles_PreservesOrderAndProtectsSnapshot()
    {
        using var fixture = CleanFixture();
        string[] files = ["z.cs", "A.cs", "z.cs"];
        var calls = 0;
        var memo = new GateReadyChangedFilesMemo(ResolveCommit, _ =>
        {
            calls++;
            return new GoalWorktreeChangedFilesResult(true, files, null);
        });

        var first = memo.Resolve(fixture.Repository, fixture.GoalId);
        files[0] = "mutated.cs";
        Assert.Throws<NotSupportedException>(() => ((IList<string>)first.Files)[0] = "caller.cs");
        var second = memo.Resolve(fixture.Repository, fixture.GoalId);

        Assert.True(second.Succeeded);
        Assert.Null(second.FailureReason);
        Assert.Equal(new[] { "z.cs", "A.cs", "z.cs" }, second.Files);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void Resolve_DifferentGoalsAtSameCommits_ShareOneEntry()
    {
        using var fixture = CleanFixture();
        var alias = GoalId.New();
        AdditiveConflictGitFixture.Git(fixture.Repository, "branch", GoalWorktrees.BranchName(alias),
            GoalWorktrees.BranchName(fixture.GoalId));
        var counter = new DiffCounter();
        var memo = new GateReadyChangedFilesMemo(ResolveCommit,
            goalId => GoalWorktrees.ResolveChangedFilesAgainstHead(fixture.Repository, goalId, counter.Run));

        var first = memo.Resolve(fixture.Repository, fixture.GoalId);
        var second = memo.Resolve(fixture.Repository, alias);

        Assert.True(first.Succeeded);
        Assert.Equal(Uncached(fixture).Files, first.Files);
        Assert.Equal(first, second);
        Assert.Equal(1, counter.Count);
    }

    [Fact]
    public void Resolve_ConcurrentCalls_ComputeSuccessfulPairOnce()
    {
        using var fixture = CleanFixture();
        var calls = 0;
        var memo = new GateReadyChangedFilesMemo(
            (_, revision) => revision == "HEAD" ? fixture.MainHead : fixture.OriginalHead,
            _ =>
            {
                Interlocked.Increment(ref calls);
                return new GoalWorktreeChangedFilesResult(true, [Workers + "GoalOnly.cs"], null);
            });

        Parallel.For(0, 32, _ =>
            Assert.True(memo.Resolve(fixture.Repository, fixture.GoalId).Succeeded));

        Assert.Equal(1, calls);
    }

    private static AdditiveConflictGitFixture CleanFixture() => new(
    [
        new(Workers + "MainOnly.cs", "base\n", "main\n", "base\n"),
        new(Workers + "GoalOnly.cs", "base\n", "base\n", "goal\n")
    ]);

    private static GateReadyCandidateProjector Projector(AdditiveConflictGitFixture fixture, DiffCounter counter) =>
        GateReadyCandidateProjector.CreateForRepository(fixture.Repository, "main", changedFilesGitRunner: counter.Run);

    private static GateReadyCandidateInput Input(AdditiveConflictGitFixture fixture) =>
        new(fixture.GoalId, GoalLifecycleState.Verified, true, ChangeRiskTier.DocsOnly, ConductorTransitionDecision.Auto);

    private static GateReadyCandidateProjection Ready(
        GateReadyCandidateProjector projector, AdditiveConflictGitFixture fixture) =>
        Assert.IsType<GateReadyCandidateProjectionResult.Ready>(projector.Project(Input(fixture))).Projection;

    private static GoalWorktreeChangedFilesResult Uncached(AdditiveConflictGitFixture fixture)
    {
        var result = GoalWorktrees.ResolveChangedFilesAgainstHead(fixture.Repository, fixture.GoalId);
        Assert.True(result.Succeeded, result.FailureReason);
        return result;
    }

    private static string? ResolveCommit(string directory, string revision) =>
        GitRevisionReader.Shared.TryResolve(directory, revision, out var id) ? id : null;

    private sealed class DiffCounter
    {
        internal int Count { get; private set; }

        internal GitCli.GitResult Run(string directory, string[] arguments)
        {
            Assert.Equal("diff", arguments[0]);
            Count++;
            return GitCli.Run(directory, arguments);
        }
    }
}
