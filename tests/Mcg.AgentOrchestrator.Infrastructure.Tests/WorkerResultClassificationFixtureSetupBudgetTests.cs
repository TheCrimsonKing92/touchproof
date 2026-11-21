using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using static FixtureGitLaunchBudget;
using static WorkerDispatchTestsWorkerResultClassification;

public sealed class WorkerResultClassificationFixtureSetupBudgetTests : WorkerDispatchTestSupport
{
    private static readonly DateTimeOffset CommitTime = DateTimeOffset.Parse("2026-06-02T12:01:00Z");

    [Theory]
    [InlineData("no-commit")]
    [InlineData("prepared-copy")]
    [InlineData("fixed-time")]
    [InlineData("clock-relative")]
    [InlineData("qwen")]
    [InlineData("real-git-control")]
    public void ClassSetup_StaysWithinRealGitLaunchBudget(string scenario)
    {
        var (root, seed) = Measure("setup: dispatch seed construction and validation", CreateSeededDispatchRepository);
        try
        {
            // Up to five seed operations, five template validations and three
            // validation probes for each of staging and publication.
            Assert.InRange(seed.Total, 0, 16);
            var clock = new TestClock(CommitTime.AddMinutes(-1));
            var (dispatch, setup) = Measure($"setup: dispatch {scenario}", () =>
                CreateCompletedGoalWorktreeDispatch(root, AgentRole.Developer, string.Empty, "Committed {commit}", clock,
                    scenario == "no-commit" ? null : worktree => ArrangeCommit(worktree, scenario)));

            // Ensure accounts for up to three launches. A cold existing prepared copy
            // can validate its seed copy (six probes) and use add and commit.
            var max = scenario == "prepared-copy" ? 11L : scenario == "real-git-control" ? 5L : 3L;
            Assert.InRange(setup.Total, 1, max);
            var maxCommitProbes = scenario is "prepared-copy" or "real-git-control" ? 1L : 0L;
            Assert.InRange(setup.Count("add"), 0, maxCommitProbes);
            Assert.InRange(setup.Count("commit"), 0, maxCommitProbes);
            if (scenario == "real-git-control")
            {
                Assert.Equal(1, setup.Count("add"));
                Assert.Equal(1, setup.Count("commit"));
            }

            _ = Measure("product: refresh completed dispatch", () =>
            {
                new BackgroundDispatchRunner(clock).RefreshLatestProcess(dispatch.Kernel, dispatch.Goal.Id, dispatch.Task.Id);
                return dispatch.Task.Status;
            });
        }
        finally { LandingExecutorTests.TryDeleteDirectory(root); }
    }

    [Theory]
    [InlineData("fixed-time")]
    [InlineData("clock-relative")]
    [InlineData("qwen")]
    public void ClassSetup_ReadsBackWithRealGit(string scenario)
    {
        var root = CreateSeededDispatchRepository();
        var twin = CreateSeededDispatchRepository();
        try
        {
            var id = GoalId.New();
            var worktree = GoalWorktrees.Ensure(root, id);
            var twinWorktree = GoalWorktrees.Ensure(twin, id);
            var head = ArrangeCommit(worktree, scenario);
            var (path, content, message, time) = Input(scenario);
            WriteFile(twinWorktree, path, content);
            CommitWithRealGit(twinWorktree, message, time);

            Assert.Equal(Git(twinWorktree, "rev-parse", "HEAD"), head);
            var main = Git(twin, "rev-parse", "HEAD");
            AssertTree(root, head, main, Entry(path, content));
            Assert.Equal("Dispatch Tests <tests@example.com>", Git(worktree, "log", "-1", "--format=%an <%ae>"));
            AssertWorktree(root, worktree, id, head);
            AssertMain(root, main);
        }
        finally
        {
            LandingExecutorTests.TryDeleteDirectory(twin);
            LandingExecutorTests.TryDeleteDirectory(root);
        }
    }

    private static string ArrangeCommit(string worktree, string scenario)
    {
        var (path, content, message, time) = Input(scenario);
        if (scenario == "prepared-copy")
        {
            CopyPreparedDispatchCommit(worktree, path, content, message);
            return new FixtureRepositoryWriter(worktree).ReadRef(
                new FixtureRepositoryWriter(worktree).HeadReference()!)!;
        }

        WriteFile(worktree, path, content);
        if (scenario != "real-git-control") return CommitDispatchSetup(worktree, path, message, time);
        CommitWithRealGit(worktree, message, time);
        return new FixtureRepositoryWriter(worktree).ReadRef(new FixtureRepositoryWriter(worktree).HeadReference()!)!;
    }

    private static (string Path, string Content, string Message, DateTimeOffset Time) Input(string scenario) =>
        scenario switch
        {
            "qwen" => (".qwen/settings.json", "{}", "Qwen settings noise", CommitTime),
            "clock-relative" => ("feature.txt", "feature", "Feature", CommitTime.AddMinutes(-4)),
            _ => ("feature.txt", "feature", "Feature", CommitTime)
        };

    private static void WriteFile(string root, string path, string content)
    {
        var file = Path.Combine(root, path);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, content);
    }

    private static void CommitWithRealGit(string worktree, string message, DateTimeOffset time)
    {
        using var realGit = InProcessGitFunnel.Observe(realOnly: true);
        RunGit(worktree, ["add", "-A"], time);
        RunGit(worktree, ["commit", "-m", message], time);
    }
}
