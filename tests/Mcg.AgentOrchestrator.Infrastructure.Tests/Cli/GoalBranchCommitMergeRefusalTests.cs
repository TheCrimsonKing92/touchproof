using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: each test owns its real Git repository, worktree and SQLite stores.
public sealed class GoalBranchCommitMergeRefusalTests
{
    [Fact]
    public void PendingMerge_CommitPath_RefusesBeforeAnyMutation()
        => AssertPendingMergeRefused(resume: false);

    [Fact]
    public void PendingMerge_ResumePath_RefusesBeforeAnyMutation()
        => AssertPendingMergeRefused(resume: true);

    [Fact]
    public void CompletedTwoParentCommit_ResumePath_RefusesWithoutIntents()
    {
        using var fixture = new GoalBranchCommitTestFixture();
        fixture.StartPendingMerge();
        fixture.Git("commit", "-m", "Reviewed merge", "-m", "Operator-Commit: goal-branch-commit");
        var beforeHead = fixture.Head;
        Assert.NotEqual(fixture.PreviousHead, beforeHead);
        Assert.Equal(3, fixture.Git("rev-list", "--parents", "-n", "1", "HEAD")
            .Split(' ', StringSplitOptions.RemoveEmptyEntries).Length);
        Assert.Equal("goal-branch-commit", fixture.Git("log", "-1", "--format=%(trailers:key=Operator-Commit,valueonly)"));
        var mergeHead = GitCli.RunForRepository(fixture.Worktree, "rev-parse", "-q", "--verify", "MERGE_HEAD");
        Assert.True(mergeHead.ProcessStarted);
        Assert.False(mergeHead.DrainTimedOut);
        Assert.Equal(1, mergeHead.ExitCode);
        Assert.Empty(mergeHead.Error);
        var beforeStatus = fixture.Git("status", "--porcelain=v1");
        var beforeState = fixture.StateJson();
        Assert.Empty(fixture.Intents());

        var result = fixture.Run(resume: true);

        Assert.Equal(1, result.ExitCode);
        Assert.Equal("Error: ResumeNotApplicable: HEAD must have exactly one parent.", result.Error.Trim());
        Assert.DoesNotContain("New head:", result.Output);
        Assert.Equal(beforeHead, fixture.Head);
        Assert.Equal(beforeStatus, fixture.Git("status", "--porcelain=v1"));
        Assert.Empty(fixture.Intents());
        Assert.Equal(beforeState, fixture.StateJson());
    }

    private static void AssertPendingMergeRefused(bool resume)
    {
        using var fixture = new GoalBranchCommitTestFixture();
        fixture.StartPendingMerge();
        var beforeHead = fixture.Head;
        var beforeMergeHead = fixture.Git("rev-parse", "-q", "--verify", "MERGE_HEAD");
        var beforeStatus = fixture.Git("status", "--porcelain=v1");
        var beforeState = fixture.StateJson();
        Assert.Empty(fixture.Intents());

        var result = fixture.Run(resume);

        Assert.Equal(1, result.ExitCode);
        Assert.Equal("Error: MergeInProgress: The goal worktree has an unfinished merge. Complete it with an ordinary reviewed git merge --continue, or use git merge --abort to drop it. goal-branch-commit only records one-parent commits.",
            result.Error.Trim());
        Assert.DoesNotContain("New head:", result.Output);
        Assert.Equal(beforeHead, fixture.Head);
        Assert.Equal(2, fixture.Git("rev-list", "--parents", "-n", "1", "HEAD")
            .Split(' ', StringSplitOptions.RemoveEmptyEntries).Length);
        Assert.Equal(beforeMergeHead, fixture.Git("rev-parse", "-q", "--verify", "MERGE_HEAD"));
        Assert.Equal(beforeStatus, fixture.Git("status", "--porcelain=v1"));
        Assert.Empty(fixture.Intents());
        Assert.Equal(beforeState, fixture.StateJson());
    }
}
