using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;
using Fixture = GoalRepositoryWorktreeProvisionerFixture;

// Parallel-safe: every case owns unique temporary repositories and goal resources.
public sealed class GoalRepositoryWorktreeProvisionerIdempotenceTests
{
    [Fact]
    public void TryProvision_SecondCall_ReturnsSameBindingsWithoutChangingResourcesOrWork()
    {
        using var alpha = new Fixture();
        using var beta = new Fixture("trunk");
        var goal = GoalId.New();
        var set = new GoalRepositorySet(["beta", "alpha"]);
        var checkouts = Fixture.Checkouts(alpha, beta);
        Assert.True(GoalRepositoryWorktreeProvisioner.TryProvision(set, goal, checkouts, out var first, out var firstReason), firstReason);
        Assert.NotNull(first);
        var path = first["alpha"].WorktreePath;
        var head = Fixture.CommitChange(path);
        File.WriteAllText(Path.Combine(path, "untracked.txt"), "preserve\n");
        var before = new[] { alpha.Branches(), alpha.Worktrees(), beta.Branches(), beta.Worktrees() };

        Assert.True(GoalRepositoryWorktreeProvisioner.TryProvision(set, goal, checkouts,
            (_, _, _, _) => throw new InvalidOperationException("Reuse must not call add"),
            out var second, out var reason), reason);

        Assert.NotNull(second);
        Assert.Equal(string.Empty, reason);
        Assert.Equal(first.OrderBy(entry => entry.Key, StringComparer.Ordinal), second.OrderBy(entry => entry.Key, StringComparer.Ordinal));
        Assert.Equal(before, new[] { alpha.Branches(), alpha.Worktrees(), beta.Branches(), beta.Worktrees() });
        Assert.Equal(head, Fixture.Run(path, "rev-parse", "HEAD"));
        Assert.Equal("preserve\n", File.ReadAllText(Path.Combine(path, "untracked.txt")));
    }

    [Fact]
    public void TryProvision_PartiallyProvisioned_OnlyAddsMissingRepository()
    {
        using var alpha = new Fixture();
        using var beta = new Fixture();
        var goal = GoalId.New();
        Assert.True(GoalRepositoryWorktreeProvisioner.TryProvision(new GoalRepositorySet(["alpha"]), goal,
            new Dictionary<string, string> { ["alpha"] = alpha.CheckoutPath }, out var first, out var firstReason), firstReason);
        Assert.NotNull(first);
        var before = new[] { alpha.Branches(), alpha.Worktrees() };
        var calls = new List<string>();

        Assert.True(GoalRepositoryWorktreeProvisioner.TryProvision(new GoalRepositorySet(["beta", "alpha"]), goal,
            Fixture.Checkouts(alpha, beta), (checkout, path, branch, start) =>
            {
                calls.Add(checkout);
                return GitCli.Run(checkout, "worktree", "add", "-b", branch, path, start);
            }, out var bindings, out var reason), reason);

        Assert.NotNull(bindings);
        Assert.Equal(new[] { beta.CheckoutPath }, calls);
        Assert.Equal(first["alpha"], bindings["alpha"]);
        Assert.True(Directory.Exists(bindings["beta"].WorktreePath));
        Assert.Equal(before, new[] { alpha.Branches(), alpha.Worktrees() });
    }

    [Theory]
    [InlineData("directory")]
    [InlineData("branch")]
    [InlineData("wrong-branch")]
    public void TryProvision_PreexistingConflict_IsPreserved(string conflict)
    {
        using var repository = new Fixture();
        var goal = GoalId.New();
        var path = repository.WorktreePath(goal, "alpha");
        var branch = GoalRepositoryWorktreeLayout.BranchName(goal, "alpha");
        if (conflict == "directory")
        {
            Directory.CreateDirectory(path);
            File.WriteAllText(Path.Combine(path, "keep.txt"), "preexisting\n");
        }
        else if (conflict == "branch") repository.Git("branch", branch);
        else repository.Git("worktree", "add", "-b", "unrelated", path, repository.IntegrationBranch);
        var before = new[] { repository.Branches(), repository.Worktrees() };

        Assert.False(GoalRepositoryWorktreeProvisioner.TryProvision(new GoalRepositorySet(["alpha"]), goal,
            new Dictionary<string, string> { ["alpha"] = repository.CheckoutPath }, out var bindings, out var reason));

        Assert.Null(bindings);
        Assert.StartsWith("alpha:layout-conflict:", reason);
        Assert.Equal(before, new[] { repository.Branches(), repository.Worktrees() });
        if (conflict == "directory") Assert.Equal("preexisting\n", File.ReadAllText(Path.Combine(path, "keep.txt")));
    }
}
