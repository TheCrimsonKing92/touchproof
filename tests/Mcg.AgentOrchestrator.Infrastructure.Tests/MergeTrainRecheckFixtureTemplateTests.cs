using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: immutable templates; every WithTrain call owns its repository and state.
public sealed class MergeTrainRecheckFixtureTemplateTests : AcceptanceCohortWorkflowTests
{
    [Theory]
    [InlineData("abandoned")]
    [InlineData("overlap")]
    public void WithTrain_SameMode_CopiesHistoryAndIsolatesWrites(string mode)
    {
        string? firstRoot = null;
        string? secondRoot = null;
        MergeTrainFocusedRecheckTests.WithTrain(mode, true, first =>
        {
            firstRoot = first.Repo;
            MergeTrainFocusedRecheckTests.WithTrain(mode, true, second =>
            {
                secondRoot = second.Repo;
                Assert.NotEqual(first.Repo, second.Repo);
                Assert.NotSame(first.Kernel, second.Kernel);
                Assert.NotSame(first.Goals[0], second.Goals[0]);
                Assert.Equal(first.Prepared, second.Prepared);
                foreach (var train in new[] { first, second })
                {
                    Assert.Equal(train.Prepared.Members.Select(member => member.GoalId),
                        train.Goals.Select(goal => goal.Id));
                    Assert.Equal(train.Prepared.Main, train.Red.Identity.ObservedMainRevision);
                    Assert.Equal(train.Prepared.Main, RunGitOutput(train.Repo, "rev-parse", "main").Trim());
                    Assert.Equal(train.Prepared.Seed, RunGitOutput(train.Repo, "rev-parse", "main~2").Trim());
                    foreach (var member in train.Prepared.Members)
                    {
                        Assert.Equal(member.Revision,
                            RunGitOutput(train.Repo, "rev-parse", GoalWorktrees.BranchName(member.GoalId)).Trim());
                        var worktree = Assert.IsType<string>(GoalWorktrees.TryResolve(train.Repo, member.GoalId));
                        Assert.Equal(member.Revision, RunGitOutput(worktree, "rev-parse", "HEAD").Trim());
                        Assert.Equal(NormalizePath(worktree),
                            NormalizePath(RunGitOutput(worktree, "rev-parse", "--show-toplevel").Trim()));
                        Assert.Equal(NormalizePath(Path.Combine(train.Repo, ".git")),
                            NormalizePath(RunGitOutput(worktree, "rev-parse", "--path-format=absolute", "--git-common-dir").Trim()));
                        var gitDirectory = RunGitOutput(worktree, "rev-parse", "--absolute-git-dir").Trim();
                        Assert.Equal(NormalizePath(Path.Combine(worktree, ".git")),
                            NormalizePath(File.ReadAllText(Path.Combine(gitDirectory, "gitdir")).Trim()));
                    }
                }

                var template = MergeTrainRecheckFixtureTemplate.TemplateDirectory(mode);
                var firstWorktree = Assert.IsType<string>(GoalWorktrees.TryResolve(first.Repo, first.Goals[0].Id));
                var relativeWorktree = Path.GetRelativePath(first.Repo, firstWorktree);
                var head = File.ReadAllText(Path.Combine(second.Repo, ".git", "HEAD"));
                File.WriteAllText(Path.Combine(first.Repo, "copy-only.txt"), "isolated root");
                File.WriteAllText(Path.Combine(firstWorktree, "copy-only.txt"), "isolated worktree");
                File.WriteAllText(Path.Combine(first.Repo, ".git", "HEAD"), first.Prepared.Main + "\n");
                foreach (var untouched in new[] { second.Repo, template })
                {
                    Assert.False(File.Exists(Path.Combine(untouched, "copy-only.txt")));
                    Assert.False(File.Exists(Path.Combine(untouched, relativeWorktree, "copy-only.txt")));
                    Assert.Equal(head, File.ReadAllText(Path.Combine(untouched, ".git", "HEAD")));
                }
            });
            Assert.NotNull(secondRoot);
            Assert.False(Directory.Exists(secondRoot));
            Assert.True(Directory.Exists(first.Repo));
        });
        Assert.NotNull(firstRoot);
        Assert.False(Directory.Exists(firstRoot));
        Assert.True(Directory.Exists(MergeTrainRecheckFixtureTemplate.TemplateDirectory(mode)));
    }

    [Theory]
    [InlineData("abandoned")]
    [InlineData("overlap")]
    public void WithTrain_WarmCall_LaunchesAtMostThreeGitProcesses(string mode)
    {
        MergeTrainFocusedRecheckTests.WithTrain(mode, true, _ => { });
        using var launches = InfrastructureTestSupport.CountGitProbeLaunches();
        MergeTrainFocusedRecheckTests.WithTrain(mode, true, _ => { });
        Assert.InRange(launches.Count, 0, 3);
    }
}
