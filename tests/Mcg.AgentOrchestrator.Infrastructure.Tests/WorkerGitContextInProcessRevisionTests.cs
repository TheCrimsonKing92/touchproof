using Mcg.AgentOrchestrator.Infrastructure;
using LaunchRecording = InProcessGitFixture.LaunchRecording;

// Parallel-safe: fixture-owned repositories and test-identity-scoped launch windows.
public sealed class WorkerGitContextInProcessRevisionTests
{
    [Theory]
    [InlineData("loose")]
    [InlineData("packed")]
    [InlineData("linked")]
    public void Contexts_MatchGitWithoutRevParse(string layout)
    {
        using var fixture = new InProcessGitFixture(layout);
        var integrationBranch = fixture.Branch == "trunk" ? "topic" : "trunk";
        var expectedMain = fixture.Git("rev-parse", integrationBranch);
        var expectedHead = fixture.Git("rev-parse", "HEAD");
        var expectedBranch = fixture.Git("branch", "--show-current");
        Assert.NotEqual(expectedMain, expectedHead);
        var expectedBase = fixture.Git("merge-base", integrationBranch, "HEAD");
        var expectedPaths = fixture.Git("diff", "--name-only", integrationBranch + "...HEAD", "--")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var launches = new LaunchRecording();
        Assert.Equal(expectedMain, WorkerGitContext.ReadCurrentMainIdentityForRetry(fixture.Worktree, integrationBranch));
        var target = Assert.IsType<WorkerProfileDispatcher.TargetContext>(
            WorkerProfileDispatcher.TryReadCurrentTargetContext(fixture.Worktree));
        Assert.Equal(expectedHead, target.HeadCommit);
        Assert.Equal(expectedBranch, target.BranchName);
        var scope = new WorkerGitContext().ReadReviewerChangedFileScope(fixture.Worktree, integrationBranch);
        Assert.Equal(expectedMain, scope.IntegrationCommit);
        Assert.Equal(expectedHead, scope.CandidateCommit);
        Assert.Equal(expectedBase, scope.MergeBase);
        Assert.Equal(expectedPaths, scope.ChangedFiles);
        launches.AssertRevParseCount(0, expectedBranch: 0);
        fixture.AssertRevisionParity();
    }

    [Fact]
    public void ForcedMiss_RetainsOriginalProcessLookups()
    {
        using var fixture = new InProcessGitFixture();
        var expected = fixture.Git("rev-parse", "HEAD");
        var seen = new List<string>();
        string? Miss(string directory, string revision)
        {
            Assert.Equal(fixture.Worktree, directory);
            seen.Add(revision);
            return null;
        }
        var launches = new LaunchRecording();
        Assert.Equal(expected, WorkerGitContext.ReadCurrentMainIdentityForRetry(fixture.Worktree, "trunk", Miss));
        Assert.Equal(expected, WorkerProfileDispatcher.TryReadCurrentTargetContext(fixture.Worktree, Miss)?.HeadCommit);
        Assert.Equal(expected, new WorkerGitContext().ReadReviewerChangedFileScope(fixture.Worktree, "trunk", Miss).MergeBase);
        launches.AssertRevParseCount(4);
        Assert.Equal(["trunk", "HEAD", "trunk^{commit}", "HEAD^{commit}"], seen);
    }

    [Fact]
    public void MissingRef_PreservesNullAndReviewerException()
    {
        using var fixture = new InProcessGitFixture();
        var legacy = GitCli.Run(fixture.Worktree, "rev-parse", "--verify", "absent^{commit}");
        Assert.False(legacy.Succeeded);
        Assert.Null(WorkerGitContext.ReadCurrentMainIdentityForRetry(fixture.Worktree, "absent", (_, _) => null));
        var error = Assert.Throws<ReviewerChangedFileScopeException>(() =>
            new WorkerGitContext().ReadReviewerChangedFileScope(fixture.Worktree, "absent", (_, _) => null));
        Assert.Equal(WorkerGitContext.ReviewerScopeUnavailableErrorCode, error.ErrorCode);
        Assert.Equal("Reviewer changed-file scope unavailable because git ref 'absent' could not be resolved. " +
            legacy.Error.Trim(), error.Message);
    }

    [Fact]
    public void NonAsciiBranch_UsesOriginalBranchProcessOnDecline()
    {
        using var fixture = new InProcessGitFixture();
        fixture.Git("branch", "-m", "caf\u00e9");
        var expectedBranch = fixture.Git("branch", "--show-current");
        var expectedHead = fixture.Git("rev-parse", "HEAD");
        var launches = new LaunchRecording();
        var target = Assert.IsType<WorkerProfileDispatcher.TargetContext>(
            WorkerProfileDispatcher.TryReadCurrentTargetContext(fixture.Worktree, (_, _) => null));
        Assert.Equal(expectedBranch, target.BranchName);
        Assert.Equal(expectedHead, target.HeadCommit);
        launches.AssertRevParseCount(1, expectedBranch: 1);
    }

    [Fact]
    public void DetachedAndUnbornTarget_PreserveBranchAndNullOutcomes()
    {
        using var fixture = new InProcessGitFixture();
        fixture.Git("checkout", "--detach", "HEAD");
        var expected = fixture.Git("rev-parse", "HEAD");
        var launches = new LaunchRecording();
        var target = Assert.IsType<WorkerProfileDispatcher.TargetContext>(
            WorkerProfileDispatcher.TryReadCurrentTargetContext(fixture.Worktree));
        Assert.Null(target.BranchName);
        Assert.Equal(expected, target.HeadCommit);
        launches.AssertRevParseCount(0, expectedBranch: 0);
        using var unborn = new InProcessGitFixture(commit: false);
        Assert.Null(WorkerProfileDispatcher.TryReadCurrentTargetContext(unborn.Worktree, (_, _) => null));
    }
}
