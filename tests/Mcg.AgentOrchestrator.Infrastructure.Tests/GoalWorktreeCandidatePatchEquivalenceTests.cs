using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel safe: each case owns a unique temporary repository and linked worktree;
// git configuration is repository-local and no process environment is mutated.
public sealed class GoalWorktreeCandidatePatchEquivalenceTests : HostCapacityBoundTestBase
{
    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public void RebasedCandidate_UsesGoalWorktreeAndConfiguredBranch(bool changeGoalLine)
    {
        var repository = Path.Combine(Path.GetTempPath(), "mcg-verdict-patch-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repository);
        try
        {
            const string integrationBranch = "integration";
            Git(repository, "init", "-b", integrationBranch);
            Git(repository, "config", "user.name", "Patch Test");
            Git(repository, "config", "user.email", "patch-test@example.invalid");
            File.WriteAllText(Path.Combine(repository, "goal.txt"), "before\n");
            Git(repository, "add", "goal.txt");
            Git(repository, "commit", "-m", "Base");
            var oldBase = Git(repository, "rev-parse", "HEAD");
            var goal = GoalId.New();
            var worktree = GoalWorktrees.WorktreePath(repository, goal);
            Git(repository, "worktree", "add", "-b", GoalWorktrees.BranchName(goal), worktree);
            Assert.Equal(worktree, GoalWorktrees.TryResolve(repository, goal));
            File.WriteAllText(Path.Combine(worktree, "goal.txt"), "goal change\n");
            Git(worktree, "add", "goal.txt");
            Git(worktree, "commit", "-m", "Goal work");
            var reviewedSha = Git(worktree, "rev-parse", "HEAD");

            File.WriteAllText(Path.Combine(repository, "integration.txt"), "new integration work\n");
            Git(repository, "add", "integration.txt");
            Git(repository, "commit", "-m", "Advance integration branch");
            var newBase = Git(repository, "rev-parse", "HEAD");
            Git(worktree, "rebase", integrationBranch);
            if (changeGoalLine)
            {
                File.WriteAllText(Path.Combine(worktree, "goal.txt"), "different goal change\n");
                Git(worktree, "add", "goal.txt");
                Git(worktree, "commit", "--amend", "--no-edit");
            }
            var currentSha = Git(worktree, "rev-parse", "HEAD");
            Assert.NotEqual(reviewedSha, currentSha);
            Assert.NotEqual(oldBase, newBase);
            Assert.Equal(oldBase, Git(worktree, "merge-base", integrationBranch, reviewedSha));
            Assert.Equal(newBase, Git(worktree, "merge-base", integrationBranch, currentSha));

            var seam = new GoalWorktreeCandidatePatchEquivalence(repository, integrationBranch);
            Assert.Equal(!changeGoalLine, seam.AreEquivalent(goal, reviewedSha, currentSha, out var evidence));
            if (!changeGoalLine) Assert.False(string.IsNullOrWhiteSpace(evidence));
            Assert.False(seam.AreEquivalent(GoalId.New(), reviewedSha, currentSha, out _));
            Assert.False(seam.AreEquivalent(goal, "missing-sha", currentSha, out _));
            Assert.False(new GoalWorktreeCandidatePatchEquivalence(repository, "missing-branch")
                .AreEquivalent(goal, reviewedSha, currentSha, out _));
        }
        finally
        {
            LandingExecutorTests.TryDeleteDirectory(repository);
        }
    }

    [Xunit.Fact]
    public void InvalidContext_ReturnsFalseWithoutThrowing()
    {
        var seam = new GoalWorktreeCandidatePatchEquivalence("\0", "integration");
        Assert.False(seam.AreEquivalent(GoalId.New(), new string('a', 40), new string('b', 40), out var evidence));
        Assert.Empty(evidence);
    }

    private static string Git(string directory, params string[] arguments)
    {
        var result = GitCli.Run(directory, arguments);
        Assert.True(result.Succeeded, $"git {string.Join(' ', arguments)}: {result.Error}");
        return result.Output.Trim();
    }
}
