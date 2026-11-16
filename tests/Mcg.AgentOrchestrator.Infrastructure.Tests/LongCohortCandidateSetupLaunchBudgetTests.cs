using Mcg.AgentOrchestrator.Infrastructure;
using static GoalRepositoryWorktreeProvisionerFixtureSeedTests;

// Parallel-safe: instance-owned template, independent roots and AsyncLocal-filtered launch observation.
public sealed class LongCohortCandidateSetupLaunchBudgetTests : GoalWorktreeTestBase
{
    private const int MaximumGitLaunches = 2;
    private const int UnixIndexRefreshesPerCopy = 3;

    [Fact]
    public void Copy_WarmRecipe_PreservesCandidatesWithinLaunchBudget()
    {
        using var template = new CohortCandidateTemplate();
        var copies = new List<CohortCandidateTemplate.PreparedRepository>();
        try
        {
            var warm = AcceptanceCohortWorkflowBackgroundSupport.CopyLongTrainCandidates(template);
            copies.Add(warm);
            CohortCandidateTemplate.PreparedRepository first;
            CohortCandidateTemplate.PreparedRepository second;
            var launches = new GitLaunches();
            using (launches)
            {
                first = AcceptanceCohortWorkflowBackgroundSupport.CopyLongTrainCandidates(template);
                copies.Add(first);
                second = AcceptanceCohortWorkflowBackgroundSupport.CopyLongTrainCandidates(template);
                copies.Add(second);
            }

            // Unix ctime cannot be copied; each copy refreshes its root and two worktree indexes.
            var maximumGitLaunches = MaximumGitLaunches
                + (OperatingSystem.IsWindows() ? 0 : 2 * UnixIndexRefreshesPerCopy);
            var commands = launches.Commands;
            Assert.True(commands.Length <= maximumGitLaunches,
                $"Expected at most {maximumGitLaunches} git launches for two warm long-train candidate copies; got {commands.Length}: {string.Join(", ", commands)}");

            // A separate real launch proves the budget observer is live without consuming the budget.
            var control = new GitLaunches();
            using (control)
                RealGit(first.Repo, "rev-parse", "HEAD");
            Assert.Equal(new[] { "rev-parse" }, control.Commands);

            Assert.NotEqual(warm.Repo, first.Repo);
            Assert.NotEqual(warm.Repo, second.Repo);
            Assert.NotEqual(first.Repo, second.Repo);
            Assert.Equal(2, first.Revisions.Length);
            Assert.Equal(2, second.Revisions.Length);
            Assert.Equal(warm.Revisions, first.Revisions);
            Assert.Equal(first.Revisions, second.Revisions);
            Assert.Equal(first.GoalIds, second.GoalIds);
            foreach (var copy in new[] { first, second })
            {
                Assert.True(File.Exists(Path.Combine(copy.Repo, "config", "acceptance-manifest.json")));
                for (var index = 0; index < 2; index++)
                {
                    var goalId = copy.GoalIds[index];
                    Assert.Equal(copy.Revisions[index],
                        RealGit(copy.Repo, "rev-parse", $"refs/heads/{GoalWorktrees.BranchName(goalId)}"));
                    var worktree = Assert.IsType<string>(GoalWorktrees.TryResolve(copy.Repo, goalId));
                    Assert.Equal(GoalWorktrees.WorktreePath(copy.Repo, goalId), worktree);
                    var relativePath = index == 0
                        ? "src/Mcg.AgentOrchestrator.Infrastructure/First.cs"
                        : "tests/Second.cs";
                    Assert.Equal(index == 0 ? "first" : "second",
                        File.ReadAllText(Path.Combine(worktree, relativePath.Replace('/', Path.DirectorySeparatorChar))));
                }
            }
            for (var index = 0; index < 2; index++)
                Assert.NotEqual(GoalWorktrees.TryResolve(first.Repo, first.GoalIds[index]),
                    GoalWorktrees.TryResolve(second.Repo, second.GoalIds[index]));
        }
        finally
        {
            foreach (var copy in copies) DeleteDirectory(copy.Repo);
        }
    }
}
