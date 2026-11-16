using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Repository = GoalWorktreeIsolatedDotnetTestsGitFactIndex.Repository;

// Serial: the repository helper installs a thread-local git environment observer.
// Every case owns and removes its real repository; this prefix selects ProcessSpawning.
[Xunit.Collection(TestCollections.ProcessSpawning)]
public sealed class GoalWorktreeIsolatedDotnetTestsBranchTreeContent
{
    [Xunit.Fact]
    public void BuildGoalBranchFacts_CombinedCherryPickAndLaterMainEdit_ReclassifiesContent()
    {
        using var repo = new Repository();
        repo.Run("checkout", "-b", "main");
        var goal = CreateVerifiedGoal();
        var branch = GoalWorktrees.BranchName(goal.Id);
        repo.Run("checkout", "-b", branch);
        File.WriteAllText(Path.Combine(repo.Root, "a.txt"), "first\n");
        CommitAll(repo, "first branch change");
        var first = repo.Run("rev-parse", "HEAD").Trim();
        File.WriteAllText(Path.Combine(repo.Root, "a.txt"), "second\n");
        File.WriteAllBytes(Path.Combine(repo.Root, "space Ω.bin"), [0, 255, 128, 10, 13, 0]);
        CommitAll(repo, "second branch change");
        var tip = repo.Run("rev-parse", "HEAD").Trim();
        repo.Run("checkout", "main");
        repo.Commit("unrelated");
        repo.Run("cherry-pick", "--no-commit", first, tip);
        CommitStaged(repo, "combined cherry pick");
        var landed = repo.Run("rev-parse", "HEAD").Trim();

        // Pin the regression precondition: neither original patch id is upstream,
        // although the final blob content (including a binary file) is identical.
        var landedPatch = PatchId(repo, landed);
        Assert.NotEqual(landedPatch, PatchId(repo, first));
        Assert.NotEqual(landedPatch, PatchId(repo, tip));
        var cherry = GitCli.Run(repo.Root, "cherry", landed, branch);
        Assert.Equal(GoalBranchContentState.AbsentFromMain, GoalGitFactIndex.ClassifyCherryResult(cherry));
        Assert.Equal(2, cherry.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Count(line => line.StartsWith("+ ")));

        var cache = new GoalBranchContentStateCache();
        var index = GoalGitFactIndex.Build(repo.Root, "main", branchContentStateCache: cache);
        Assert.Equal(tip, index.TryGetGoalBranchTip(goal.Id));
        Assert.Equal(GoalBranchContentState.EquivalentToMain, index.BuildGoalBranchFacts(goal).ContentState);
        Assert.Equal(GoalBranchContentState.AbsentFromMain, index.BuildReplacementFacts(goal).ContentState);
        var completed = Goal.FromSnapshot(goal.ToSnapshot() with { Status = GoalStatus.Completed });
        var completedIndex = GoalGitFactIndex.Build(repo.Root, "main", branchContentStateCache: cache);
        Assert.Equal(GoalBranchContentState.AbsentFromMain, completedIndex.BuildGoalBranchFacts(completed).ContentState);

        File.WriteAllText(Path.Combine(repo.Root, "a.txt"), "later main edit\n");
        CommitAll(repo, "edit landed path on main");
        var advanced = GoalGitFactIndex.Build(repo.Root, "main", branchContentStateCache: cache);
        Assert.NotEqual(index.MainSha, advanced.MainSha);
        Assert.Equal(GoalBranchContentState.AbsentFromMain, advanced.BuildGoalBranchFacts(goal).ContentState);
        Assert.Equal(GoalBranchContentState.AbsentFromMain, Detect(repo, advanced.MainSha!, branch, tip));
    }

    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public void BuildGoalBranchFacts_DifferentOrMissingMainPath_RemainsAbsent(bool presentOnMain)
    {
        using var repo = new Repository();
        repo.Run("checkout", "-b", "main");
        var goal = CreateVerifiedGoal();
        var branch = GoalWorktrees.BranchName(goal.Id);
        repo.Run("checkout", "-b", branch);
        File.WriteAllText(Path.Combine(repo.Root, "a.txt"), "branch content\n");
        CommitAll(repo, "branch addition");
        repo.Run("checkout", "main");
        if (presentOnMain) File.WriteAllText(Path.Combine(repo.Root, "a.txt"), "different main content\n");
        repo.Commit("unrelated");
        var cherry = GitCli.Run(repo.Root, "cherry", "main", branch);
        Assert.Equal(GoalBranchContentState.AbsentFromMain, GoalGitFactIndex.ClassifyCherryResult(cherry));

        var index = GoalGitFactIndex.Build(repo.Root, "main");
        Assert.Equal(GoalBranchContentState.AbsentFromMain, index.BuildGoalBranchFacts(goal).ContentState);
        Assert.Equal(GoalBranchContentState.AbsentFromMain, Detect(repo, index.MainSha!, branch, index.TryGetGoalBranchTip(goal.Id)!));
    }

    [Xunit.Fact]
    public void Classify_RenameDeletionAndModeChange_UsesBothNamesAndIgnoresModes()
    {
        using var repo = new Repository();
        repo.Run("config", "core.filemode", "false");
        File.WriteAllText(Path.Combine(repo.Root, "old name.txt"), "same blob\n");
        File.WriteAllText(Path.Combine(repo.Root, "remove.txt"), "remove me\n");
        CommitAll(repo, "shared files");
        repo.Run("checkout", "-b", "main");
        const string branch = "goal/tree-paths";
        repo.Run("checkout", "-b", branch);
        repo.Run("mv", "old name.txt", "new name.txt");
        repo.Run("rm", "remove.txt");
        repo.Run("update-index", "--chmod=+x", "new name.txt");
        CommitStaged(repo, "rename delete and make executable");
        var tip = repo.Run("rev-parse", "HEAD").Trim();
        Assert.StartsWith("100755 ", repo.Run("ls-tree", tip, "--", "new name.txt"));
        repo.Run("checkout", "main");
        repo.Run("mv", "old name.txt", "new name.txt");
        repo.Run("rm", "remove.txt");
        repo.Run("update-index", "--chmod=-x", "new name.txt");
        CommitStaged(repo, "same paths with regular mode");
        var main = repo.Run("rev-parse", "HEAD").Trim();
        Assert.StartsWith("100644 ", repo.Run("ls-tree", main, "--", "new name.txt"));
        Assert.Equal(GoalBranchContentState.EquivalentToMain, Detect(repo, main, branch, tip));

        File.WriteAllText(Path.Combine(repo.Root, "old name.txt"), "same blob\n");
        CommitAll(repo, "restore old name on main");
        main = repo.Run("rev-parse", "HEAD").Trim();
        Assert.Equal(GoalBranchContentState.AbsentFromMain, Detect(repo, main, branch, tip));
    }

    private static GoalBranchContentState Detect(Repository repo, string main, string branch, string tip) =>
        GoalBranchTreeContentDetector.Classify(repo.Root, main, branch, tip,
            (directory, args) => GitCli.Run(directory, args.ToArray()));

    private static Goal CreateVerifiedGoal()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Tree content regression", [new TaskSpec(TaskId.New(), "Work", AgentRole.Developer)]);
        return Goal.FromSnapshot(goal.ToSnapshot() with { Status = GoalStatus.Verified });
    }

    private static string PatchId(Repository repo, string commit)
    {
        var patch = repo.Run("show", "--pretty=format:", "--binary", commit);
        var result = GitCli.RunWithStandardInput(repo.Root, GitCli.DefaultTimeoutMilliseconds, patch, "patch-id", "--stable");
        Assert.True(result.Succeeded, result.ToString());
        var id = result.Output.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)[0];
        Assert.Matches("^[0-9a-f]{40}([0-9a-f]{24})?$", id);
        return id;
    }

    private static void CommitAll(Repository repo, string message)
    {
        repo.Run("add", "--all");
        CommitStaged(repo, message);
    }

    private static void CommitStaged(Repository repo, string message) =>
        repo.Run("-c", "user.name=Tests", "-c", "user.email=tests@example.invalid",
            "-c", "commit.gpgsign=false", "commit", "-m", message);
}
