using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each integration case owns its repository, worktree and repository-local configuration.
public sealed class PreLandingConflictRemedyTests : GoalWorktreeTestBase
{
    [Fact]
    public void Build_ListsPathsInSuppliedOrderAndNamesConductLoop()
    {
        string[] paths = ["z/last.cs", "a/first.cs", "m/mid.cs"];

        var remedy = PreLandingConflictRemedy.Build("abcd1234", paths);

        Assert.Equal(
            "Conflicting paths (in order): z/last.cs, a/first.cs, m/mid.cs. " +
            "Run conduct abcd1234 --loop. " +
            "The conductor runs one conflict-integration round per candidate head; " +
            "only if that round fails does the operator integrate main into the goal branch.",
            remedy);
        var previousIndex = -1;
        foreach (var path in paths)
        {
            var index = remedy.IndexOf(path, StringComparison.Ordinal);
            Assert.True(index > previousIndex, $"Expected {path} after the preceding conflict path: {remedy}");
            previousIndex = index;
        }
        Assert.Contains("conduct abcd1234 --loop", remedy, StringComparison.Ordinal);
        Assert.DoesNotContain("operator task", remedy, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MainIntoBranchMergeConflict_CarriesSharedRemedy()
    {
        using var fixture = new AdditiveConflictGitFixture(
        [
            new("src/Widget.cs", "base\n", "main\n", "goal\n"),
            new("src/Alpha.cs", "base\n", "main\n", "goal\n")
        ]);

        var result = GoalBranchMainMerge.TryMergeMainIntoGoalBranch(fixture.Repository, fixture.GoalId);

        Assert.Equal(GoalWorktreeRebaseStatus.Conflict, result.Status);
        Assert.Equal(new[] { "src/Alpha.cs", "src/Widget.cs" }, result.ConflictFiles);
        Assert.Equal(
            PreLandingConflictRemedy.Build(fixture.GoalId.Value[..8].ToLowerInvariant(), result.ConflictFiles),
            result.SuggestedCommand);
        Assert.DoesNotContain("operator task", result.SuggestedCommand!, StringComparison.OrdinalIgnoreCase);
        fixture.AssertRestored();
    }

    [Fact]
    public void NonConflictingRebase_ReturnsRebasedWithoutRemedy() =>
        AssertNonConflictingOperation(mergeMain: false);

    [Fact]
    public void NonConflictingMainMerge_ReturnsRebasedWithoutRemedy() =>
        AssertNonConflictingOperation(mergeMain: true);

    private static void AssertNonConflictingOperation(bool mergeMain)
    {
        var repo = CreateSeededRepository();
        try
        {
            var goalId = GoalId.New();
            var worktree = CreateGoalBranchScenario(repo, goalId, "feature.txt", "goal work").Worktree;
            File.WriteAllText(Path.Combine(repo, "main-advanced.txt"), "main work");
            RunGit(repo, "add", "main-advanced.txt");
            RunGit(repo, "commit", "-m", "Main work");

            var result = mergeMain
                ? GoalBranchMainMerge.TryMergeMainIntoGoalBranch(repo, goalId)
                : GoalWorktrees.TryRebaseOntoMain(repo, goalId);

            var prefix = goalId.Value[..8].ToLowerInvariant();
            Assert.Equal(GoalWorktreeRebaseStatus.Rebased, result.Status);
            Assert.Empty(result.ConflictFiles);
            Assert.Equal($"acceptance {prefix}", result.SuggestedCommand);
            Assert.DoesNotContain("Conflicting paths", result.SuggestedCommand!, StringComparison.Ordinal);
            Assert.DoesNotContain("--loop", result.SuggestedCommand!, StringComparison.Ordinal);
            Assert.NotEqual(PreLandingConflictRemedy.Build(prefix, result.ConflictFiles), result.SuggestedCommand);
            if (mergeMain) Assert.Equal("merged-main", result.Detail);
            Assert.Equal("goal work", File.ReadAllText(Path.Combine(worktree, "feature.txt")));
            Assert.Equal("main work", File.ReadAllText(Path.Combine(worktree, "main-advanced.txt")));
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }
}
