using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: immutable templates; every WithTrain call owns its repository and state.
public sealed class MergeTrainRecheckFixtureTemplateTests(ITestOutputHelper output) : AcceptanceCohortWorkflowTests
{
    [Theory]
    [InlineData("abandoned")]
    [InlineData("overlap")]
    public void WithTrain_SameMode_CopiesHistoryAndIsolatesWrites(string mode)
    {
        string? firstRoot = null;
        string? secondRoot = null;
        using var launches = InfrastructureTestSupport.CountGitProbeLaunches();
        var firstObservation = new MergeTrainFixtureObservation(mode, output.WriteLine, probes: () => launches.Count);
        var secondObservation = new MergeTrainFixtureObservation(mode, output.WriteLine,
            probes: () => launches.Count, parentId: firstObservation.Id);
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
            }, secondObservation);
            Assert.NotNull(secondRoot);
            Assert.False(Directory.Exists(secondRoot));
            Assert.True(Directory.Exists(first.Repo));
        }, firstObservation);
        Assert.NotNull(firstRoot);
        Assert.False(Directory.Exists(firstRoot));
        Assert.True(Directory.Exists(MergeTrainRecheckFixtureTemplate.TemplateDirectory(mode)));
    }

    [Theory]
    [InlineData("abandoned")]
    [InlineData("overlap")]
    public void WithTrain_WarmCall_LaunchesAtMostThreeGitProcesses(string mode)
    {
        using (var initialLaunches = InfrastructureTestSupport.CountGitProbeLaunches())
            MergeTrainFocusedRecheckTests.WithTrain(mode, true, _ => { },
                new(mode, output.WriteLine, probes: () => initialLaunches.Count));
        using var launches = InfrastructureTestSupport.CountGitProbeLaunches();
        MergeTrainFocusedRecheckTests.WithTrain(mode, true, _ => { },
            new(mode, output.WriteLine, probes: () => launches.Count));
        Assert.InRange(launches.Count, 0, 3);
    }

    [Fact]
    public void WarmCopy_PreservesIndexesWithoutWindowsRefreshLaunch()
    {
        var first = MergeTrainRecheckFixtureTemplate.Copy("abandoned");
        string? secondRoot = null;
        try
        {
            using var launches = InfrastructureTestSupport.CountGitProbeLaunches();
            var before = launches.Count;
            var second = MergeTrainRecheckFixtureTemplate.Copy("abandoned");
            secondRoot = second.Repo;
            Assert.Equal(OperatingSystem.IsWindows() ? 0 : 1, launches.Count - before);
            foreach (var copy in new[] { first, second })
            {
                Assert.Empty(RunGitOutput(copy.Repo, "status", "--porcelain"));
                Assert.Equal(RunGitOutput(first.Repo, "ls-files", "-s"), RunGitOutput(copy.Repo, "ls-files", "-s"));
                foreach (var member in copy.History.Members)
                {
                    var worktree = GoalWorktrees.WorktreePath(copy.Repo, member.GoalId);
                    Assert.Empty(RunGitOutput(worktree, "status", "--porcelain"));
                    // Product Git inherits host config; fixture probes intentionally exclude it.
                    // Both newline policies must see the same clean checkout and tracked bytes.
                    var sharedPath = Path.Combine(worktree, MergeTrainFocusedRecheckTests.SharedPath);
                    File.Delete(sharedPath);
                    var checkout = GitCli.Run(worktree, "-c", "core.autocrlf=true", "checkout-index", "--force", "--",
                        MergeTrainFocusedRecheckTests.SharedPath);
                    Assert.True(checkout.Succeeded, checkout.Error);
                    Assert.Empty(RunGitOutput(worktree, "status", "--porcelain"));
                    foreach (var autoCrlf in new[] { "false", "true" })
                    {
                        var status = GitCli.Run(worktree, "-c", "core.autocrlf=" + autoCrlf, "status", "--porcelain");
                        Assert.True(status.Succeeded, status.Error);
                        Assert.Empty(status.Output);
                    }
                    Assert.Equal(RunGitOutput(worktree, "show", "HEAD:" + MergeTrainFocusedRecheckTests.SharedPath),
                        File.ReadAllText(sharedPath).Trim());
                    Assert.Equal(member.Revision, RunGitOutput(worktree, "rev-parse", "HEAD").Trim());
                }
            }
        }
        finally
        {
            DeleteDirectory(first.Repo);
            if (secondRoot is not null) DeleteDirectory(secondRoot);
        }
    }
}
