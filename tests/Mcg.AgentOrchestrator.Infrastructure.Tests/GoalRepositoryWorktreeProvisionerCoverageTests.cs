using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;
using Fixture = GoalRepositoryWorktreeProvisionerFixture;

// Parallel-safe: every case owns unique temporary repositories and goal resources.
public sealed class GoalRepositoryWorktreeProvisionerCoverageTests
{
    [Theory]
    [InlineData("missing", "beta:missing-binding")]
    [InlineData("extra", "gamma:unexpected-binding")]
    [InlineData("extra-case", "Alpha:unexpected-binding")]
    [InlineData("case-insensitive-map", "alpha:missing-binding")]
    public void TryProvision_KeyMismatch_DoesNotMutateAnyRepository(string scenario, string expected)
    {
        using var alpha = new Fixture();
        using var beta = new Fixture();
        var goal = GoalId.New();
        var checkouts = new Dictionary<string, string>(Fixture.Checkouts(alpha, beta), StringComparer.OrdinalIgnoreCase);
        if (scenario == "missing") checkouts.Remove("beta");
        if (scenario == "extra") checkouts.Add("gamma", alpha.CheckoutPath);
        if (scenario == "extra-case")
            checkouts = new Dictionary<string, string>(Fixture.Checkouts(alpha, beta), StringComparer.Ordinal) { ["Alpha"] = alpha.CheckoutPath };
        if (scenario == "case-insensitive-map")
        {
            checkouts.Remove("alpha");
            checkouts.Add("Alpha", alpha.CheckoutPath);
        }
        var before = new[] { alpha.Branches(), alpha.Worktrees(), beta.Branches(), beta.Worktrees() };

        Assert.False(GoalRepositoryWorktreeProvisioner.TryProvision(new GoalRepositorySet(["beta", "alpha"]), goal,
            checkouts, out var bindings, out var reason));

        Assert.Null(bindings);
        Assert.Equal(expected, reason);
        Assert.Equal(before, new[] { alpha.Branches(), alpha.Worktrees(), beta.Branches(), beta.Worktrees() });
        alpha.AssertAbsent(goal, "alpha");
        beta.AssertAbsent(goal, "beta");
    }

    [Theory]
    [InlineData("missing-directory")]
    [InlineData("non-repository")]
    [InlineData("unborn")]
    [InlineData("detached")]
    public void TryProvision_InvalidCheckout_FailsWithKeyBeforeMutation(string scenario)
    {
        using var alpha = new Fixture();
        using var beta = new Fixture();
        var goal = GoalId.New();
        var checkouts = Fixture.Checkouts(alpha, beta);
        if (scenario == "detached") beta.Git("checkout", "--detach");
        else
        {
            var path = Path.Combine(beta.Root, "invalid");
            if (scenario != "missing-directory") Directory.CreateDirectory(path);
            if (scenario == "unborn") Fixture.Run(path, "init", "-b", "main");
            checkouts["beta"] = path;
        }
        var before = new[] { alpha.Branches(), alpha.Worktrees(), beta.Branches(), beta.Worktrees() };

        Assert.False(GoalRepositoryWorktreeProvisioner.TryProvision(new GoalRepositorySet(["alpha", "beta"]), goal,
            checkouts, out var bindings, out var reason));

        Assert.Null(bindings);
        Assert.StartsWith("beta:", reason);
        Assert.Equal(before, new[] { alpha.Branches(), alpha.Worktrees(), beta.Branches(), beta.Worktrees() });
        alpha.AssertAbsent(goal, "alpha");
    }
}
