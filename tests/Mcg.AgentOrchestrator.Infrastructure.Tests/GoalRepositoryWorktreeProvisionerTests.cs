using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;
using Fixture = GoalRepositoryWorktreeProvisionerFixture;

// Parallel-safe: every case owns unique temporary repositories and goal resources.
public sealed class GoalRepositoryWorktreeProvisionerTests
{
    [Fact]
    public void TryProvision_TwoRepositories_CreatesLayoutBranchesAndComputableTuple()
    {
        using var alpha = new Fixture("main");
        using var beta = new Fixture("trunk");
        var set = new GoalRepositorySet(["beta", "alpha"]);
        var goal = GoalId.New();
        var checkouts = Fixture.Checkouts(alpha, beta);
        var heads = checkouts.ToDictionary(entry => entry.Key, entry => Fixture.Run(entry.Value, "rev-parse", "HEAD"));

        Assert.True(GoalRepositoryWorktreeProvisioner.TryProvision(set, goal, checkouts, out var bindings, out var reason), reason);

        Assert.Equal(string.Empty, reason);
        Assert.NotNull(bindings);
        Assert.Equal(set.Keys, bindings.Keys);
        foreach (var (key, fixture) in new[] { ("alpha", alpha), ("beta", beta) })
        {
            var path = fixture.WorktreePath(goal, key);
            Assert.Equal((path, fixture.IntegrationBranch), bindings[key]);
            Assert.True(Directory.Exists(path));
            Assert.Equal(GoalRepositoryWorktreeLayout.BranchName(goal, key), Fixture.Run(path, "symbolic-ref", "--short", "HEAD"));
            Assert.Equal(heads[key], Fixture.Run(path, "rev-parse", "HEAD"));
            Fixture.CommitChange(path);
        }

        Assert.True(GoalRepositoryCandidateTuples.TryCompute(set, bindings, out var tuple, out var failure,
            (_, _) => "manifest"), failure);
        Assert.True(set.CoversExactly(tuple));
        Assert.Equal(heads["alpha"], alpha.Git("rev-parse", "HEAD"));
        Assert.Equal(heads["beta"], beta.Git("rev-parse", "HEAD"));
    }

    [Fact]
    public void TryProvision_TwoKeysForOneRepository_UsesDistinctLayoutResources()
    {
        using var repository = new Fixture();
        var goal = GoalId.New();
        var checkouts = new Dictionary<string, string> { ["alpha"] = repository.CheckoutPath, ["beta"] = repository.CheckoutPath };
        Assert.True(GoalRepositoryWorktreeProvisioner.TryProvision(new GoalRepositorySet(["alpha", "beta"]), goal,
            checkouts, out var bindings, out var reason), reason);
        Assert.NotNull(bindings);
        Assert.NotEqual(bindings["alpha"].WorktreePath, bindings["beta"].WorktreePath);
        foreach (var key in bindings.Keys)
            Assert.Equal(GoalRepositoryWorktreeLayout.BranchName(goal, key),
                Fixture.Run(bindings[key].WorktreePath, "symbolic-ref", "--short", "HEAD"));
    }

    [Fact]
    public void TryProvision_NullArguments_Throws()
    {
        var set = new GoalRepositorySet(["alpha"]);
        var goal = GoalId.New();
        var checkouts = new Dictionary<string, string>();
        Assert.Throws<ArgumentNullException>(() => GoalRepositoryWorktreeProvisioner.TryProvision(null!, goal, checkouts, out _, out _));
        Assert.Throws<ArgumentNullException>(() => GoalRepositoryWorktreeProvisioner.TryProvision(set, null!, checkouts, out _, out _));
        Assert.Throws<ArgumentNullException>(() => GoalRepositoryWorktreeProvisioner.TryProvision(set, goal, null!, out _, out _));
    }
}
