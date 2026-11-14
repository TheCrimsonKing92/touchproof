using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: private linked worktree; observer chains other observers and filters by test identity.
public sealed class ConductorStewardCaseDSourcesTests
{
    [Fact]
    public void DefaultBranchHead_ReturnsWorktreeHeadWithoutRevParseLaunch()
    {
        using var fixture = new GitRefFileResolverParityTests.Fixture();
        var goal = NewGoal();
        var worktree = GoalWorktreeLayout.WorktreePath(fixture.Repo, goal.Id);
        fixture.Git("worktree", "add", "-b", GoalWorktreeLayout.BranchName(goal.Id), worktree);
        fixture.Git("commit", "--allow-empty", "-m", "main advances");
        Assert.True(File.Exists(Path.Combine(worktree, ".git")));
        var sources = ConductorStewardCaseDSources.CreateDefault(Workspace(fixture.Repo));
        var launches = new List<string>();
        var testName = HostCapacitySlotTimingWriter.CurrentTestName();
        Assert.False(string.IsNullOrEmpty(testName));
        Assert.NotEqual("<unknown>", testName);
        Action<string>? previous;
        Action<string> observer;
        do
        {
            previous = Volatile.Read(ref GitCli.LaunchObserver);
            var chained = previous;
            observer = command =>
            {
                chained?.Invoke(command);
                if (HostCapacitySlotTimingWriter.CurrentTestName() == testName) launches.Add(command);
            };
        } while (Interlocked.CompareExchange(ref GitCli.LaunchObserver, observer, previous) != previous);
        try
        {
            var oracle = GitCli.Run(worktree, "rev-parse", "HEAD");
            Assert.True(oracle.Succeeded, oracle.Error);
            Assert.Equal("rev-parse", Assert.Single(launches));
            Assert.NotEqual(fixture.Oracle("HEAD"), oracle.Output.Trim());
            launches.Clear();
            Assert.Equal(oracle.Output.Trim(), sources.BranchHead(goal));
            Assert.DoesNotContain("rev-parse", launches);
        }
        finally { Interlocked.CompareExchange(ref GitCli.LaunchObserver, previous, observer); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DefaultBranchHead_ReturnsNullForMissingOrUnresolvableWorktree(bool unresolvable)
    {
        using var fixture = new GitRefFileResolverParityTests.Fixture();
        var goal = NewGoal();
        if (unresolvable)
        {
            var worktree = GoalWorktreeLayout.WorktreePath(fixture.Repo, goal.Id);
            fixture.Git("worktree", "add", "-b", GoalWorktreeLayout.BranchName(goal.Id), worktree);
            var gitFile = File.ReadAllText(Path.Combine(worktree, ".git")).Trim();
            var gitDirectory = gitFile["gitdir: ".Length..];
            File.WriteAllText(Path.Combine(gitDirectory, "HEAD"), "ref: refs/heads/missing-fixture-branch\n");
        }
        var sources = ConductorStewardCaseDSources.CreateDefault(Workspace(fixture.Repo));
        Assert.Null(sources.ResolveHead(goal));
    }

    private static Goal NewGoal() => new(GoalId.New(), "Steward head fixture",
        [new TaskSpec(TaskId.New(), "Fixture", AgentRole.Developer)]);

    private static OrchestratorWorkspace Workspace(string root) => new(root, root,
        Path.Combine(root, ".orchestrator"), "fixture", false, "default", false,
        "", "", "", "", "", "", "");
}
