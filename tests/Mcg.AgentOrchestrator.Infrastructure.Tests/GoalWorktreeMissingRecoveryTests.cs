using Mcg.AgentOrchestrator.Infrastructure;
using Mcg.AgentOrchestrator.Core;

public sealed class GoalWorktreeMissingRecoveryTests : GoalWorktreeRecoveryTestBase
{
    [Xunit.Theory]
    [Xunit.InlineData(false, false)]
    [Xunit.InlineData(false, true)]
    [Xunit.InlineData(true, false)]
    [Xunit.InlineData(true, true)]
    public void MissingCheckoutRestoresExactExistingTip(bool stale, bool uniqueCommit)
    {
        using var fixture = new MissingFixture(stale, uniqueCommit);
        var result = fixture.Recover();
        Assert.True(result.Succeeded, result.Observation);
        Assert.True(result.Recreated);
        Assert.Equal(fixture.Tip, result.PreservedTip);
        fixture.AssertRestored();
        Assert.Equal(uniqueCommit, File.Exists(Path.Combine(fixture.Path, "unique-goal.txt")));
        Assert.False(File.Exists(Path.Combine(fixture.Path, "new-main.txt")));
        Assert.Equal(result, Assert.Single(fixture.Receipts));
    }

    [Xunit.Fact]
    public void RestartReobservesAndReusesCompletedRecovery()
    {
        using var fixture = new MissingFixture();
        Assert.True(fixture.Recover().Recreated);
        // No in-memory completion receipt is supplied to the second caller.
        var second = GoalWorktrees.RecoverMissing(fixture.Repo, fixture.Goal.Id, () => null, _ => { });
        Assert.True(second.Succeeded, second.Observation);
        Assert.False(second.Recreated);
        fixture.AssertRestored();
    }

    [Xunit.Fact]
    public void InitialPreparationRetainsOrdinaryCreation()
    {
        var repo = CreateSeededRepository();
        try
        {
            var id = GoalId.New();
            var before = RunGitOutput(repo, "rev-parse", "HEAD").Trim();
            var receipts = new List<GoalWorktreeRecoveryResult>();
            var path = GoalWorktrees.PrepareForDispatch(repo, id, allowInitialCreation: true, () => null, receipts.Add);
            Assert.Equal(GoalWorktrees.WorktreePath(repo, id), path);
            Assert.Equal(before, RunGitOutput(path, "rev-parse", "HEAD").Trim());
            Assert.Equal(GoalWorktrees.BranchName(id), RunGitOutput(path, "branch", "--show-current").Trim());
            Assert.Empty(receipts);
        }
        finally { DeleteDirectory(repo); }
    }

    [Xunit.Fact]
    public void RecoveryLeavesUnrelatedRegistrationAndDirtyContentIntact()
    {
        using var f = new MissingFixture();
        var other = Path.Combine(f.Repo, "unrelated-worktree");
        RunGit(f.Repo, "worktree", "add", "-b", "unrelated", other, f.MainBranch);
        File.WriteAllText(Path.Combine(other, "dirty.txt"), "other workspace content");
        var before = Assert.Single(GoalWorktreeRegistrationListing.Read(f.Repo, new()).Records.Where(r => r.Branch == "refs/heads/unrelated"));
        Assert.True(f.Recover().Succeeded);
        Assert.Equal(before, Assert.Single(GoalWorktreeRegistrationListing.Read(f.Repo, new()).Records.Where(r => r.Branch == "refs/heads/unrelated")));
        Assert.Equal("other workspace content", File.ReadAllText(Path.Combine(other, "dirty.txt")));
        f.AssertRestored();
    }
}
