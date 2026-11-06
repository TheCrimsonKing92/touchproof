using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each test owns its repository; census windows use the current test identity.
public sealed class GoalWorktreeEnsureLaunchBudgetTests(ITestOutputHelper output) : GoalWorktreeTestBase
{
    [Fact]
    public void Ensure_AbsentBranch_LaunchesOnlyWorktreeAdd()
    {
        var repo = CreateSeededRepository();
        try
        {
            var goal = GoalId.New();
            Assert.False(BranchExists(repo, GoalWorktrees.BranchName(goal)));
            AssertSeededFileProofs(repo, GoalWorktrees.BranchName(goal), absent: true);
            var (path, launches) = EnsureWithLaunches(repo, goal);
            AssertBudget(launches);
            Assert.Equal(GoalWorktrees.BranchName(goal), RunGitOutput(path, "branch", "--show-current").Trim());
        }
        finally { DeleteDirectory(repo); }
    }

    [Fact]
    public void Ensure_ExistingBranch_LaunchesOnlyWorktreeAdd()
    {
        var repo = CreateSeededRepository();
        try
        {
            var goal = GoalId.New();
            var branch = GoalWorktrees.BranchName(goal);
            RunGit(repo, "branch", branch);
            Assert.True(BranchExists(repo, branch));
            var expectedHead = RunGitOutput(repo, "rev-parse", "refs/heads/" + branch).Trim();
            AssertSeededFileProofs(repo, branch, absent: false);
            var (path, launches) = EnsureWithLaunches(repo, goal);
            AssertBudget(launches);
            Assert.Equal(branch, RunGitOutput(path, "branch", "--show-current").Trim());
            Assert.Equal(expectedHead, RunGitOutput(path, "rev-parse", "HEAD").Trim());
        }
        finally { DeleteDirectory(repo); }
    }

    [Fact]
    public void Ensure_NotAWorkTree_ThrowsExistingDiagnostic()
    {
        var repo = CreateSeededRepository();
        try
        {
            // Invalid metadata stops discovery even if the test temp root is inside a checkout.
            var directory = Directory.CreateDirectory(Path.Combine(repo, "not-work-tree")).FullName;
            File.WriteAllText(Path.Combine(directory, ".git"), "gitdir: missing\n");
            var expected = Assert.Throws<InvalidOperationException>(() => GoalWorktrees.HasBranch(directory, GoalId.New()));
            var actual = Assert.Throws<InvalidOperationException>(() => GoalWorktrees.Ensure(directory, GoalId.New()));
            Assert.Equal(expected.Message, actual.Message);
            Assert.StartsWith($"Goal workspaces require '{directory}' to be inside a git work tree. " +
                "probe='git rev-parse --is-inside-work-tree'; exit=128;", actual.Message);
            Assert.Contains("processStarted=True", actual.Message);
            Assert.Contains("directoryExists=True", actual.Message);
            Assert.Contains("not a git repository", actual.Message);
        }
        finally { DeleteDirectory(repo); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Ensure_ExcludeEntry_AddedWhenMissingAndUnchangedWhenPresent(bool present)
    {
        var repo = CreateSeededRepository();
        try
        {
            var exclude = Path.Combine(repo, ".git", "info", "exclude");
            var original = ".orchestrator/\n" + (present ? "  .orchestrator-worktrees/  \r\n# keep bytes" : "");
            File.WriteAllText(exclude, original, new System.Text.UTF8Encoding(false));
            var bytes = File.ReadAllBytes(exclude);
            GoalWorktrees.Ensure(repo, GoalId.New());
            if (present) Assert.Equal(bytes, File.ReadAllBytes(exclude));
            else Assert.Equal(original + ".orchestrator-worktrees/" + Environment.NewLine, File.ReadAllText(exclude));
            Assert.Single(File.ReadAllLines(exclude), line => line.Trim() == ".orchestrator-worktrees/");
        }
        finally { DeleteDirectory(repo); }
    }

    private static void AssertSeededFileProofs(string repo, string branch, bool absent)
    {
        Assert.Contains("[user]", File.ReadAllText(Path.Combine(repo, ".git", "config")));
        Assert.True(GitRevisionReader.TryConfirmWorkTree(repo));
        Assert.True(GitRevisionReader.TryResolveRootGitDirectory(repo, out _));
        Assert.Equal(absent, GitRevisionReader.TryConfirmBranchAbsent(repo, branch));
    }

    private void AssertBudget(HostCapacitySlotGitCensus.GitLaunchDelta launches)
    {
        int Count(string command) => launches.Subcommands.GetValueOrDefault(command);
        var readers = Count("cat-file");
        output.WriteLine($"creation: rev-parse={Count("rev-parse")}, worktree={Count("worktree")}; post-creation cat-file starts={readers}");
        // Literal identity configuration no longer requires creation or HEAD probes.
        Assert.Equal(0, Count("rev-parse"));
        Assert.Equal(1, Count("worktree"));
        Assert.Equal(0, readers);
        Assert.Equal(0, Count("status"));
        Assert.Equal(0, Count("merge"));
        Assert.Equal(Count("rev-parse") + Count("worktree") + readers, launches.Launches);
    }

    private static (string Path, HostCapacitySlotGitCensus.GitLaunchDelta Launches) EnsureWithLaunches(string repo, GoalId goal)
    {
        var identity = HostCapacitySlotTimingWriter.CurrentTestName();
        Assert.NotEqual("<unknown>", identity);
        HostCapacitySlotGitCensus.EnsureInstalled(nameof(GoalWorktreeEnsureLaunchBudgetTests));
        var window = Assert.IsType<HostCapacitySlotGitCensus.Window>(HostCapacitySlotGitCensus.Shared.Open(identity));
        string path;
        HostCapacitySlotGitCensus.GitLaunchDelta? launches;
        try { path = GoalWorktrees.Ensure(repo, goal); }
        finally { launches = window.Close(); }
        return (path, Assert.IsType<HostCapacitySlotGitCensus.GitLaunchDelta>(launches));
    }
}
