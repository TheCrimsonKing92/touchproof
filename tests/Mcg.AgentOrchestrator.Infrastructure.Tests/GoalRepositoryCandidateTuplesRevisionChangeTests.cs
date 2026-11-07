using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;
using RepositoryFixture = GoalRepositoryCandidateTuplesTests.RepositoryFixture;

// Parallel-safe: both repositories and linked worktrees belong exclusively to this case.
public sealed class GoalRepositoryCandidateTuplesRevisionChangeTests
{
    [Fact]
    public void TryCompute_OneRepositoryCommitChanges_ChangesOnlyItsCandidate()
    {
        using var alpha = new RepositoryFixture();
        using var beta = new RepositoryFixture();
        var repositories = new GoalRepositorySet(["alpha", "beta"]);
        var bindings = GoalRepositoryCandidateTuplesTests.Bindings(alpha, beta);
        var alphaHead = alpha.Head;
        var betaHead = beta.Head;
        Assert.True(GoalWorktrees.TryResolveCandidateMergeBase(alpha.WorktreePath, "main", out var alphaBase));
        Assert.True(GoalWorktrees.TryResolveCandidateMergeBase(beta.WorktreePath, "main", out var betaBase));
        Assert.NotEqual(alphaBase, alphaHead);
        Assert.NotEqual(betaBase, betaHead);
        Assert.True(GoalRepositoryCandidateTuples.TryCompute(repositories, bindings,
            out var before, out var beforeReason, (_, _) => "manifest"), beforeReason);
        Assert.NotNull(before);

        beta.CommitChange("value = 3\n");

        Assert.Equal(alphaHead, alpha.Head);
        Assert.NotEqual(betaHead, beta.Head);
        Assert.True(GoalRepositoryCandidateTuples.TryCompute(repositories, bindings,
            out var after, out var afterReason, (_, _) => "manifest"), afterReason);
        Assert.NotNull(after);
        Assert.False(RepositoryCandidateTuple.AreSameCandidateSet(before, after));
        var beforeEntries = before.Entries.ToDictionary(entry => entry.Key, entry => entry.Value);
        var afterEntries = after.Entries.ToDictionary(entry => entry.Key, entry => entry.Value);
        Assert.Equal(beforeEntries["alpha"], afterEntries["alpha"]);
        Assert.NotEqual(beforeEntries["beta"], afterEntries["beta"]);
    }
}
