using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;
using Fixture = GoalRepositoryWorktreeProvisionerFixture;

// Parallel-safe: every case owns unique temporary repositories and goal resources.
public sealed class GoalRepositoryWorktreeProvisionerRollbackTests
{
    [Fact]
    public void TryProvision_SecondGitAddFails_RemovesFirstWorktreeAndBranch()
    {
        using var alpha = new Fixture();
        using var beta = new Fixture();
        var goal = GoalId.New();
        var blocker = GoalRepositoryWorktreeLayout.BranchName(goal, "beta") + "/blocker";
        beta.Git("branch", blocker);
        var before = new[] { alpha.Branches(), alpha.Worktrees(), beta.Branches(), beta.Worktrees() };

        Assert.False(GoalRepositoryWorktreeProvisioner.TryProvision(new GoalRepositorySet(["beta", "alpha"]), goal,
            Fixture.Checkouts(alpha, beta), out var bindings, out var reason));

        Assert.Null(bindings);
        Assert.StartsWith("beta:worktree-add-failed:", reason);
        Assert.DoesNotContain("cleanup-residual", reason);
        alpha.AssertAbsent(goal, "alpha");
        beta.AssertAbsent(goal, "beta");
        Assert.Equal(before, new[] { alpha.Branches(), alpha.Worktrees(), beta.Branches(), beta.Worktrees() });
        beta.Git("show-ref", "--verify", "refs/heads/" + blocker);
    }

    [Theory]
    [InlineData("failure")]
    [InlineData("exception")]
    [InlineData("drain-timeout")]
    public void TryProvision_AddLeavesResourcesBeforeFailure_RollsBackFailingKeyToo(string failure)
    {
        using var alpha = new Fixture();
        using var beta = new Fixture();
        var goal = GoalId.New();
        var calls = new List<string>();
        var observedFirstWorktree = false;
        var before = new[] { alpha.Branches(), alpha.Worktrees(), beta.Branches(), beta.Worktrees() };

        Assert.False(GoalRepositoryWorktreeProvisioner.TryProvision(new GoalRepositorySet(["beta", "alpha"]), goal,
            Fixture.Checkouts(alpha, beta), (checkout, path, branch, start) =>
            {
                calls.Add(checkout);
                var added = GitCli.Run(checkout, "worktree", "add", "-b", branch, path, start);
                Assert.True(added.Succeeded && !added.DrainTimedOut, added.Error);
                if (checkout != beta.CheckoutPath) return added;
                observedFirstWorktree = Directory.Exists(alpha.WorktreePath(goal, "alpha"));
                Assert.True(observedFirstWorktree);
                Assert.True(Directory.Exists(path));
                if (failure == "exception") throw new IOException("injected after add");
                return failure == "drain-timeout"
                    ? added with { DrainTimedOut = true }
                    : new GitCli.GitResult(1, added.Output, "injected after add");
            }, out var bindings, out var reason));

        Assert.True(observedFirstWorktree);
        Assert.Equal(new[] { alpha.CheckoutPath, beta.CheckoutPath }, calls);
        Assert.Null(bindings);
        Assert.StartsWith("beta:worktree-add-failed:", reason);
        Assert.DoesNotContain("cleanup-residual", reason);
        alpha.AssertAbsent(goal, "alpha");
        beta.AssertAbsent(goal, "beta");
        Assert.Equal(before, new[] { alpha.Branches(), alpha.Worktrees(), beta.Branches(), beta.Worktrees() });
    }

    [Fact]
    public void TryProvision_LockedNewWorktree_ReportsResidualsAndContinuesOtherCleanup()
    {
        using var alpha = new Fixture();
        using var beta = new Fixture();
        var goal = GoalId.New();
        var betaPath = beta.WorktreePath(goal, "beta");
        var betaBranch = GoalRepositoryWorktreeLayout.BranchName(goal, "beta");
        var locked = false;

        Assert.False(GoalRepositoryWorktreeProvisioner.TryProvision(new GoalRepositorySet(["alpha", "beta"]), goal,
            Fixture.Checkouts(alpha, beta), (checkout, path, branch, start) =>
            {
                var added = GitCli.Run(checkout, "worktree", "add", "-b", branch, path, start);
                Assert.True(added.Succeeded && !added.DrainTimedOut, added.Error);
                if (checkout != beta.CheckoutPath) return added;
                Fixture.Run(checkout, "worktree", "lock", path, "--reason", "deterministic cleanup failure");
                locked = true;
                return new GitCli.GitResult(1, added.Output, "injected after lock");
            }, out var bindings, out var reason));

        Assert.True(locked);
        Assert.Null(bindings);
        Assert.StartsWith("beta:worktree-add-failed:injected after lock", reason);
        Assert.Contains(";cleanup-residual:" + betaPath + ":", reason);
        Assert.Contains(";cleanup-residual:" + betaBranch + ":", reason);
        Assert.Contains("locked", reason);
        alpha.AssertAbsent(goal, "alpha");
        Assert.Contains("branch refs/heads/" + betaBranch, beta.Worktrees());
    }

    [Fact]
    public void TryProvision_LaterFailure_PreservesReusedWorktreeAndItsChanges()
    {
        using var alpha = new Fixture();
        using var beta = new Fixture();
        var goal = GoalId.New();
        Assert.True(GoalRepositoryWorktreeProvisioner.TryProvision(new GoalRepositorySet(["alpha"]), goal,
            new Dictionary<string, string> { ["alpha"] = alpha.CheckoutPath }, out var original, out var initialReason), initialReason);
        Assert.NotNull(original);
        var path = original["alpha"].WorktreePath;
        var head = Fixture.CommitChange(path);
        File.WriteAllText(Path.Combine(path, "untracked.txt"), "keep this\n");
        beta.Git("branch", GoalRepositoryWorktreeLayout.BranchName(goal, "beta") + "/blocker");
        var before = new[] { alpha.Branches(), alpha.Worktrees(), beta.Branches(), beta.Worktrees() };

        Assert.False(GoalRepositoryWorktreeProvisioner.TryProvision(new GoalRepositorySet(["alpha", "beta"]), goal,
            Fixture.Checkouts(alpha, beta), out var bindings, out var reason));

        Assert.Null(bindings);
        Assert.StartsWith("beta:worktree-add-failed:", reason);
        Assert.Equal(head, Fixture.Run(path, "rev-parse", "HEAD"));
        Assert.Equal("keep this\n", File.ReadAllText(Path.Combine(path, "untracked.txt")));
        Assert.Equal(before, new[] { alpha.Branches(), alpha.Worktrees(), beta.Branches(), beta.Worktrees() });
        beta.AssertAbsent(goal, "beta");
    }
}
