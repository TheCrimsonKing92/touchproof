using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;
using Fixture = GoalRepositoryWorktreeProvisionerFixture;

// Parallel-safe: every case owns unique temporary repositories and goal resources.
public sealed class GoalRepositoryBindingResolverTests
{
    [Fact]
    public void TryResolve_ProvisionedSet_MatchesBindingsAndComputesCoveringTuple()
    {
        using var alpha = new Fixture("main");
        using var beta = new Fixture("trunk");
        var set = new GoalRepositorySet(["beta", "alpha"]);
        var goal = GoalId.New();
        var checkouts = Fixture.Checkouts(alpha, beta);
        Assert.True(GoalRepositoryWorktreeProvisioner.TryProvision(set, goal, checkouts,
            out var provisioned, out var provisionReason), provisionReason);
        Assert.NotNull(provisioned);
        foreach (var key in set.Keys)
            Fixture.CommitChange(provisioned[key].WorktreePath);
        var before = new[] { Snapshot(alpha), Snapshot(beta) };

        Assert.True(GoalRepositoryBindingResolver.TryResolve(set, goal, checkouts,
            out var resolved, out var reason), reason);

        Assert.Equal(string.Empty, reason);
        Assert.NotNull(resolved);
        Assert.Equal(set.Keys, resolved.Keys);
        foreach (var key in set.Keys)
            Assert.Equal(provisioned[key], resolved[key]);
        Assert.Equal(before[0], Snapshot(alpha));
        Assert.Equal(before[1], Snapshot(beta));
        Assert.True(GoalRepositoryCandidateTuples.TryCompute(set, resolved, out var tuple,
            out var failure, (_, _) => "manifest"), failure);
        Assert.True(set.CoversExactly(tuple));
    }

    [Theory]
    [InlineData("missing", "beta:missing-binding")]
    [InlineData("case-variant", "beta:missing-binding")]
    [InlineData("unexpected", "aardvark:unexpected-binding")]
    [InlineData("missing-and-unexpected", "alpha:missing-binding")]
    public void TryResolve_InvalidCoverage_FailsBeforeInspectingWorktrees(
        string scenario, string expectedReason)
    {
        using var alpha = new Fixture();
        using var beta = new Fixture("trunk");
        var set = new GoalRepositorySet(["beta", "alpha"]);
        var checkouts = Fixture.Checkouts(alpha, beta);
        switch (scenario)
        {
            case "missing":
                checkouts.Remove("beta");
                break;
            case "case-variant":
                checkouts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    { ["alpha"] = alpha.CheckoutPath, ["Beta"] = beta.CheckoutPath };
                break;
            case "unexpected":
                checkouts["gamma"] = beta.CheckoutPath;
                checkouts["aardvark"] = alpha.CheckoutPath;
                break;
            case "missing-and-unexpected":
                checkouts.Clear();
                checkouts["aardvark"] = alpha.CheckoutPath;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(scenario));
        }

        // Neither worktree exists: map diagnostics must take precedence over that failure.
        AssertFailure(set, GoalId.New(), checkouts, expectedReason, alpha, beta);
    }

    [Fact]
    public void TryResolve_LaterWorktreeMissing_ReturnsNoPartialBindings()
    {
        using var alpha = new Fixture();
        using var beta = new Fixture("trunk");
        var set = new GoalRepositorySet(["beta", "alpha"]);
        var goal = GoalId.New();
        var checkouts = Fixture.Checkouts(alpha, beta);
        Assert.True(GoalRepositoryWorktreeProvisioner.TryProvision(set, goal, checkouts,
            out var provisioned, out var reason), reason);
        Assert.NotNull(provisioned);
        beta.Git("worktree", "remove", "--force", provisioned["beta"].WorktreePath);
        Assert.True(Directory.Exists(provisioned["alpha"].WorktreePath));
        Assert.False(Directory.Exists(provisioned["beta"].WorktreePath));

        AssertFailure(set, goal, checkouts, "beta:missing-worktree", alpha, beta);
    }

    [Fact]
    public void TryResolve_UnprovisionedSet_DoesNotCreateResources()
    {
        using var alpha = new Fixture();
        using var beta = new Fixture("trunk");
        var set = new GoalRepositorySet(["beta", "alpha"]);
        var goal = GoalId.New();

        AssertFailure(set, goal, Fixture.Checkouts(alpha, beta),
            "alpha:missing-worktree", alpha, beta);

        Assert.False(Directory.Exists(Path.Combine(alpha.CheckoutPath, ".orchestrator-worktrees")));
        Assert.False(Directory.Exists(Path.Combine(beta.CheckoutPath, ".orchestrator-worktrees")));
    }

    [Fact]
    public void TryResolve_CheckoutBranchChanged_ReadsCurrentIntegrationBranch()
    {
        using var repository = new Fixture();
        var set = new GoalRepositorySet(["repo"]);
        var goal = GoalId.New();
        var checkouts = new Dictionary<string, string> { ["repo"] = repository.CheckoutPath };
        Assert.True(GoalRepositoryWorktreeProvisioner.TryProvision(set, goal, checkouts,
            out _, out var provisionReason), provisionReason);
        repository.Git("switch", "-c", "new-integration");
        var before = Snapshot(repository);

        Assert.True(GoalRepositoryBindingResolver.TryResolve(set, goal, checkouts,
            out var bindings, out var reason), reason);

        Assert.NotNull(bindings);
        Assert.Equal((repository.WorktreePath(goal, "repo"), "new-integration"), bindings["repo"]);
        Assert.Equal(before, Snapshot(repository));
    }

    [Fact]
    public void TryResolve_NullArguments_Throws()
    {
        var set = new GoalRepositorySet(["repo"]);
        var goal = GoalId.New();
        var checkouts = new Dictionary<string, string>();
        Assert.Throws<ArgumentNullException>(() => GoalRepositoryBindingResolver.TryResolve(null!, goal, checkouts, out _, out _));
        Assert.Throws<ArgumentNullException>(() => GoalRepositoryBindingResolver.TryResolve(set, null!, checkouts, out _, out _));
        Assert.Throws<ArgumentNullException>(() => GoalRepositoryBindingResolver.TryResolve(set, goal, null!, out _, out _));
    }

    private static void AssertFailure(GoalRepositorySet set, GoalId goal,
        IReadOnlyDictionary<string, string> checkouts, string expectedReason, params Fixture[] repositories)
    {
        var before = repositories.Select(Snapshot).ToArray();

        Assert.False(GoalRepositoryBindingResolver.TryResolve(set, goal, checkouts,
            out var bindings, out var reason));

        Assert.Null(bindings);
        Assert.Equal(expectedReason, reason);
        for (var i = 0; i < repositories.Length; i++)
            Assert.Equal(before[i], Snapshot(repositories[i]));
    }

    private static string[] Snapshot(Fixture repository) =>
        new[] { repository.Branches(), repository.Worktrees() }
            .Concat(Directory.EnumerateFileSystemEntries(repository.Root, "*", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(repository.Root, path))
                .OrderBy(path => path, StringComparer.Ordinal))
            .ToArray();
}
