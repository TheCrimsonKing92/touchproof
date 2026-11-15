using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: real Git and filesystem operations are confined to a unique root per test.
public sealed class BranchWorktreeInventoryTests : IDisposable
{
    private readonly string root = SharedTestSupport.CreateTempDirectory();
    private string Repository => Path.Combine(root, "repository");
    private readonly Goal liveGoal = CreateGoal("abc10000111111111111111111111111", GoalStatus.Active);
    private readonly Goal terminalGoal = CreateGoal("def20000222222222222222222222222", GoalStatus.Completed);
    private IReadOnlyCollection<Goal> Goals => [liveGoal, terminalGoal];

    [Fact]
    public void Read_MixedRepository_ClassifiesBranchesAndWorktrees()
    {
        CreateRepository();
        Assert.False(liveGoal.IsTerminal);
        Assert.True(terminalGoal.IsTerminal);
        var inventory = BranchWorktreeInventory.Read(Repository, "trunk", Goals);

        Assert.Equal(9, inventory.Branches.Count);
        AssertBranch("trunk", "live", null, 0);
        AssertBranch("merged", "merged", null, 0);
        AssertBranch("plain", "merged", null, 0);
        AssertBranch("codex/work", "unmerged", "codex/", 3);
        AssertBranch("backup/snapshot", "unmerged", "backup/", 2);
        AssertBranch("goal/abc10000", "live", liveGoal.Id.Value, 1);
        AssertBranch("goal/abc10000--repo-r-61", "live", liveGoal.Id.Value, 1);
        AssertBranch("goal/def20000", "unmerged", terminalGoal.Id.Value, 1);
        AssertBranch("goal/eee30000", "unmerged", "eee30000", 1);
        Assert.Equal(inventory.Branches.Select(branch => branch.Name).Order(StringComparer.Ordinal),
            inventory.Branches.Select(branch => branch.Name));

        Assert.Equal(9, inventory.Worktrees.Count);
        AssertTree("repository", "other", true, null, null, "trunk");
        AssertTree("detached ü space", "other", true, null, null, null);
        AssertTree("abc10000", "goal", true, liveGoal.Id.Value, false, "goal/abc10000");
        AssertTree("def20000", "goal", true, terminalGoal.Id.Value, true, null);
        AssertTree("unknown", "goal", true, "eee30000", null, "goal/eee30000");
        AssertTree("replay", "impact_replay", true, null, null, null);
        AssertTree("baseline", "focused_evidence_baseline", true, null, null, null);
        AssertTree("canary", "canary", true, null, null, null);
        AssertTree("stale", "other", false, null, null, null);
        Assert.Equal(inventory.Worktrees.Select(tree => tree.Path).Order(StringComparer.Ordinal),
            inventory.Worktrees.Select(tree => tree.Path));

        void AssertBranch(string name, string kind, string? owner, long count) =>
            Assert.Equal((name, kind, owner, count), Assert.Single(inventory.Branches, branch => branch.Name == name));
        void AssertTree(string leaf, string kind, bool exists, string? goalId, bool? terminal, string? branch)
        {
            var tree = Assert.Single(inventory.Worktrees, tree => Path.GetFileName(tree.Path) == leaf);
            Assert.Equal((kind, exists, goalId, terminal, branch),
                (tree.Class, tree.DirectoryExists, tree.GoalId, tree.GoalTerminal, tree.Branch));
        }
    }

    [Fact]
    public void Run_StaleAndUnmergedInventory_LeavesRefsAndWorktreesUnchanged()
    {
        CreateRepository();
        var refs = Git("for-each-ref", "--format=%(refname) %(objectname)");
        var worktrees = Git("worktree", "list", "--porcelain");
        Assert.Contains("codex/work", refs);
        Assert.Contains("stale", worktrees);
        Assert.False(Directory.Exists(Path.Combine(root, "stale")));
        foreach (var args in new[] { new[] { "branch-report" }, new[] { "branch-report", "--json" } })
        {
            using var output = new StringWriter();
            using var error = new StringWriter();
            Assert.Equal(0, CliBranchReportCommand.Run(args, Repository, "trunk", () => Goals, output, error));
            Assert.Equal(string.Empty, error.ToString());
            Assert.Contains("codex/work", output.ToString());
            Assert.Contains("stale", output.ToString());
            Assert.Equal(refs, Git("for-each-ref", "--format=%(refname) %(objectname)"));
            Assert.Equal(worktrees, Git("worktree", "list", "--porcelain"));
        }
        Assert.False(Directory.Exists(Path.Combine(Repository, ".orchestrator")));
    }

    [Fact]
    public void Run_Json_PreservesSchemaCountsOrderingAndUnknownMetadata()
    {
        CreateRepository();
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(0, CliBranchReportCommand.Run(["branch-report", "--json"], Repository,
            "trunk", () => Goals, output, error));
        Assert.Equal(string.Empty, error.ToString());
        using var document = JsonDocument.Parse(output.ToString());
        var report = document.RootElement;
        Assert.Equal(new[] { "branches", "worktrees", "branch_class_counts", "worktree_class_counts" },
            report.EnumerateObject().Select(property => property.Name));
        var branches = report.GetProperty("branches").EnumerateArray().ToArray();
        Assert.Equal(9, branches.Length);
        Assert.Equal(new[] { "name", "class", "owner", "commits_not_on_main" },
            branches[0].EnumerateObject().Select(property => property.Name));
        Assert.Equal(branches.Select(branch => branch.GetProperty("name").GetString()).Order(StringComparer.Ordinal),
            branches.Select(branch => branch.GetProperty("name").GetString()));
        var codex = Assert.Single(branches, branch => branch.GetProperty("name").GetString() == "codex/work");
        Assert.Equal("unmerged", codex.GetProperty("class").GetString());
        Assert.Equal("codex/", codex.GetProperty("owner").GetString());
        Assert.Equal(3, codex.GetProperty("commits_not_on_main").GetInt32());
        Assert.Equal(JsonValueKind.Null, Assert.Single(branches,
            branch => branch.GetProperty("name").GetString() == "plain").GetProperty("owner").ValueKind);

        var trees = report.GetProperty("worktrees").EnumerateArray().ToArray();
        Assert.Equal(9, trees.Length);
        Assert.Equal(new[] { "path", "class", "directory_exists", "goal_id", "goal_terminal", "branch" },
            trees[0].EnumerateObject().Select(property => property.Name));
        Assert.Equal(trees.Select(tree => tree.GetProperty("path").GetString()).Order(StringComparer.Ordinal),
            trees.Select(tree => tree.GetProperty("path").GetString()));
        var detached = Tree("detached ü space");
        Assert.Equal(JsonValueKind.Null, detached.GetProperty("branch").ValueKind);
        Assert.Equal(JsonValueKind.Null, detached.GetProperty("goal_id").ValueKind);
        Assert.Equal(JsonValueKind.Null, detached.GetProperty("goal_terminal").ValueKind);
        Assert.False(Tree("stale").GetProperty("directory_exists").GetBoolean());
        Assert.Equal(JsonValueKind.Null, Tree("unknown").GetProperty("goal_terminal").ValueKind);
        Assert.False(Tree("abc10000").GetProperty("goal_terminal").GetBoolean());
        Assert.True(Tree("def20000").GetProperty("goal_terminal").GetBoolean());
        AssertCounts("branch_class_counts", branches, ["live", "merged", "unmerged"]);
        AssertCounts("worktree_class_counts", trees,
            ["goal", "impact_replay", "focused_evidence_baseline", "canary", "other"]);

        JsonElement Tree(string leaf) => Assert.Single(trees,
            tree => Path.GetFileName(tree.GetProperty("path").GetString()) == leaf);
        void AssertCounts(string key, JsonElement[] records, string[] classes)
        {
            var counts = report.GetProperty(key);
            Assert.Equal(classes, counts.EnumerateObject().Select(property => property.Name));
            foreach (var kind in classes)
                Assert.Equal(records.Count(record => record.GetProperty("class").GetString() == kind),
                    counts.GetProperty(kind).GetInt32());
        }
    }

    [Fact]
    public void Run_OnlyIntegrationBranch_PrintsEmptyGroupsAndZeroCounts()
    {
        CreateEmptyRepository();
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(0, CliBranchReportCommand.Run(["branch-report"], Repository, "trunk", () => [], output, error));
        Assert.Equal(string.Empty, error.ToString());
        var text = output.ToString();
        Assert.Contains("live (1)", text);
        Assert.Contains("owner=(none) | commits_not_on_main=0", text);
        foreach (var kind in new[] { "merged", "unmerged", "goal", "impact_replay", "focused_evidence_baseline", "canary" })
            Assert.Contains($"{kind} (0)", text);
        Assert.Contains("other (1)", text);
        output.GetStringBuilder().Clear();
        Assert.Equal(0, CliBranchReportCommand.Run(["branch-report", "--json"], Repository, "trunk", () => [], output, error));
        using var json = JsonDocument.Parse(output.ToString());
        Assert.Equal(0, json.RootElement.GetProperty("branch_class_counts").GetProperty("unmerged").GetInt32());
        Assert.Equal(0, json.RootElement.GetProperty("worktree_class_counts").GetProperty("goal").GetInt32());
    }

    [Fact]
    public void Read_MergedGoalAndDetachedIntegration_PreservesLivePrecedence()
    {
        CreateEmptyRepository();
        Git("branch", "goal/abc10000");
        Git("branch", "goal/def20000");
        Git("branch", "goal/eee30000");
        Git("checkout", "--detach", "trunk");
        var inventory = BranchWorktreeInventory.Read(Repository, "trunk", Goals);
        Assert.Equal(4, inventory.Branches.Count);
        Assert.All(inventory.Branches, branch => Assert.Equal(0, branch.CommitsNotOnMain));
        Assert.Equal("live", Assert.Single(inventory.Branches, branch => branch.Name == "goal/abc10000").Class);
        Assert.Equal("merged", Assert.Single(inventory.Branches, branch => branch.Name == "trunk").Class);
        Assert.Equal("merged", Assert.Single(inventory.Branches, branch => branch.Name == "goal/def20000").Class);
        Assert.Equal("merged", Assert.Single(inventory.Branches, branch => branch.Name == "goal/eee30000").Class);
    }

    [Theory]
    [InlineData("start")]
    [InlineData("drain")]
    [InlineData("exit")]
    [InlineData("malformed-ref")]
    [InlineData("missing-trunk")]
    [InlineData("missing-worktrees")]
    [InlineData("invalid-count")]
    [InlineData("goal-read")]
    public void Run_RequiredReadFails_ReturnsErrorWithoutPartialJson(string fault)
    {
        var calls = 0;
        using var output = new StringWriter();
        using var error = new StringWriter();
        var result = CliBranchReportCommand.Run(["branch-report", "--json"], root, "trunk",
            () => fault == "goal-read" ? throw new IOException("goal read failed") : [], output, error,
            (_, args) =>
            {
                calls++;
                if (fault == "start") return new(0, "", "start failed", ProcessStarted: false);
                if (fault == "drain") return new(0, "", "drain failed", DrainTimedOut: true);
                if (fault == "exit") return new(128, "", "git failed");
                if (args[0] == "rev-list") return new(0, "not-a-count", "");
                if (args[0] == "worktree") return new(0, fault == "missing-worktrees" ? "" :
                    $"worktree {root}\0HEAD {new string('a', 40)}\0branch refs/heads/trunk\0\0", "");
                if (args.Contains("--merged")) return new(0, "", "");
                return new(0, fault == "malformed-ref" ? "invalid\n" : fault == "missing-trunk" ? "" :
                    $"trunk {new string('a', 40)}\n", "");
            });
        Assert.Equal(1, result);
        Assert.Equal(string.Empty, output.ToString());
        Assert.StartsWith("Error: ", error.ToString());
        if (fault == "goal-read") Assert.Equal(0, calls);
        else Assert.True(calls > 0);
        if (fault == "missing-trunk") Assert.Contains("refs/heads/trunk", error.ToString());
        if (fault == "invalid-count") Assert.Contains("invalid count", error.ToString());
    }

    [Theory]
    [InlineData("--unknown")]
    [InlineData("unexpected")]
    public void Run_InvalidArguments_RejectsBeforeReadingGoals(string argument)
    {
        Assert.True(CliBranchReportCommand.IsCommand(["branch-report"]));
        Assert.Equal(CliCommandCapability.QueryOnly, CliCommandCapabilities.Classify(["branch-report"]));
        using var output = new StringWriter();
        using var error = new StringWriter();
        var loaded = false;
        Assert.Equal(1, CliBranchReportCommand.Run(["branch-report", argument], root, "trunk", () =>
        {
            loaded = true;
            return [];
        }, output, error));
        Assert.False(loaded);
        Assert.Equal(string.Empty, output.ToString());
        Assert.Contains(CliBranchReportCommand.Usage, error.ToString());
    }

    private static Goal CreateGoal(string id, GoalStatus status)
    {
        var goal = new Goal(new GoalId(id), "inventory fixture",
            [new TaskSpec(TaskId.New(), "fixture task", AgentRole.Developer)]);
        goal.SetStatus(status);
        return goal;
    }

    private void CreateEmptyRepository()
    {
        Directory.CreateDirectory(Repository);
        Git("-c", "init.templateDir=", "init", "--initial-branch=trunk");
        Git("config", "core.hooksPath", Path.Combine(root, "no-hooks"));
        Git("config", "commit.gpgsign", "false");
        Git("config", "user.name", "Inventory Test");
        Git("config", "user.email", "inventory@example.invalid");
        Git("commit", "--allow-empty", "-m", "base");
    }

    private void CreateRepository()
    {
        CreateEmptyRepository();
        Git("branch", "merged");
        Git("commit", "--allow-empty", "-m", "integration advance");
        Git("branch", "plain");
        AddBranch("codex/work", 3);
        AddBranch("backup/snapshot", 2);
        AddBranch("goal/abc10000", 1);
        AddBranch("goal/def20000", 1);
        AddBranch("goal/eee30000", 1);
        Git("branch", "goal/abc10000--repo-r-61", "goal/abc10000");
        AddWorktree(Path.Combine(root, "detached ü space"));
        AddWorktree(Path.Combine(root, GoalWorktreeLayout.DirectoryName, "abc10000"), "goal/abc10000");
        AddWorktree(Path.Combine(root, GoalWorktreeLayout.DirectoryName, "def20000"));
        AddWorktree(Path.Combine(root, "unknown"), "goal/eee30000");
        AddWorktree(Path.Combine(root, "impact-replay-worktrees", "replay"));
        AddWorktree(Path.Combine(root, AcceptanceTempRootNames.FocusedEvidenceBaselinesRootDirectoryName, "baseline"));
        AddWorktree(Path.Combine(root, "post-landing-canary-worktrees", "canary"));
        var stale = Path.Combine(root, "stale");
        AddWorktree(stale);
        // The registered path is a verified child of this test's unique root.
        Assert.Equal(Path.GetFullPath(root), Path.GetDirectoryName(Path.GetFullPath(stale)));
        Directory.Delete(stale, recursive: true);
    }

    private void AddBranch(string branch, int commits)
    {
        Git("checkout", "-b", branch, "trunk");
        for (var i = 0; i < commits; i++) Git("commit", "--allow-empty", "-m", $"{branch} {i}");
        Git("checkout", "trunk");
    }

    private void AddWorktree(string path, string? branch = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        Git(branch is null ? ["worktree", "add", "--detach", path, "trunk"] :
            ["worktree", "add", path, branch]);
    }

    private string Git(params string[] args)
    {
        var isolatedArgs = new[] { "-c", "core.hooksPath=" + Path.Combine(root, "no-hooks"),
            "-c", "commit.gpgsign=false" }.Concat(args).ToArray();
        var result = GitCli.RunForRepository(Repository, isolatedArgs);
        Assert.True(result.ProcessStarted && !result.DrainTimedOut && result.Succeeded,
            $"git {string.Join(' ', args)}: {result.Error}");
        return result.Output;
    }

    public void Dispose() => SharedTestSupport.RemoveTempDirectory(root);
}
