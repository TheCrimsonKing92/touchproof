using Mcg.AgentOrchestrator.Infrastructure;
using static FixtureGitLaunchBudget;

public sealed class AcceptanceCohortWorkflowTestsLongCohortRepeatedRunsSetupBudgetTests
    : AcceptanceCohortWorkflowBackgroundSupport
{
    private const string FirstPath = "src/Mcg.AgentOrchestrator.Infrastructure/First.cs";
    private const string SecondPath = "tests/Second.cs";
    private const string Manifest = "config/acceptance-manifest.json";

    [Fact]
    public void ClassSetup_StaysWithinRealGitLaunchBudget()
    {
        using var template = new CohortCandidateTemplate();
        var (cold, coldCounts) = Measure("setup: cold train recipe and first copy", () => CopyLongTrainCandidates(template));
        try
        {
            // Six seed operations, three add/commit pairs, two worktree creations
            // (up to three launches each), and at most three index refreshes.
            Assert.InRange(coldCounts.Total, 2, 21);
            var (warm, warmCounts) = Measure("setup: repeated train copy", () => CopyLongTrainCandidates(template));
            try
            {
                Assert.InRange(warmCounts.Total, 0, OperatingSystem.IsWindows() ? 0 : 3);
                Assert.NotEqual(cold.Repo, warm.Repo);
                Assert.Equal(cold.Revisions, warm.Revisions);
            }
            finally { DeleteDirectory(warm.Repo); }
        }
        finally { DeleteDirectory(cold.Repo); }

        // The product window includes the warm copy measured above. Its only possible
        // setup launches are the Unix index refreshes before the conductor starts.
        var (_, product) = Measure("product: one train gate and its warm copy", () =>
        {
            AssertLongTrainGateLandsBothGoals(template: template);
            return true;
        });
        Assert.True(product.Git.Total.Launches > 0);
    }

    [Fact]
    public void ClassSetup_ReadsBackWithRealGit()
    {
        using var template = new CohortCandidateTemplate();
        var copy = CopyLongTrainCandidates(template);
        try
        {
            var root = copy.Repo;
            var main = Git(root, "rev-parse", $"{copy.Revisions[0]}^");
            Assert.Equal(main, Git(root, "rev-parse", $"{copy.Revisions[1]}^"));
            var seed = Git(root, "rev-parse", $"{main}^");
            AssertTree(root, main, seed, Entry(Manifest, FakeAcceptanceManifest));
            AssertTree(root, copy.Revisions[0], main, Entry(FirstPath, "first"));
            AssertTree(root, copy.Revisions[1], main, Entry(SecondPath, "second"));
            for (var i = 0; i < copy.GoalIds.Length; i++)
            {
                var worktree = Assert.IsType<string>(GoalWorktrees.TryResolve(root, copy.GoalIds[i]));
                AssertWorktree(root, worktree, copy.GoalIds[i], copy.Revisions[i]);
                Assert.Equal("Worktree Tests <tests@example.com>", Git(worktree, "log", "-1", "--format=%an <%ae>"));
            }
            AssertMain(root, main);
        }
        finally { DeleteDirectory(copy.Repo); }
    }
}
