using Mcg.AgentOrchestrator.Infrastructure;
using static FixtureGitLaunchBudget;
using static PatchEquivalenceIntegrationMergeTestsAutoRebind;

public sealed class AutoRebindPatchEquivalenceFixtureSetupBudgetTests
{
    [Theory]
    [InlineData("writer", 20)]
    [InlineData("writer", 19)]
    [InlineData("writer", 17)]
    [InlineData("goal-edit", 20)]
    [InlineData("raise", 20)]
    [InlineData("foreign", 20)]
    [InlineData("dispatch", 20)]
    public void ClassSetup_StaysWithinRealGitLaunchBudget(string merge, int ceiling)
    {
        // The initial repository template can launch init, two configs, add and commit.
        var (seed, seedLaunches) = Measure("setup: landing seed template", LandingExecutorTests.CreateGitRepository);
        LandingExecutorTests.TryDeleteDirectory(seed);
        Assert.InRange(seedLaunches.Total, 0, 5);

        using var template = new PatchEquivalenceAutoRebindRepositoryTemplate();
        var (copy, counts) = Measure("setup: history, integrations and private copy", () => template.Copy(merge, ceiling));
        using (copy)
        {
            // Up to three launches for Ensure, fifteen per product integration (branch,
            // status, merge-base, merge, materialization and ratchet), and five history reads.
            Assert.InRange(counts.Total, 1, 38);
            Assert.Empty(counts.Probes);
            // Only the synthetic merge that edits goal content stages a path with real git.
            Assert.Equal(merge == "goal-edit" ? 1L : 0L, counts.Count("add"));
            var (warm, warmCounts) = Measure("setup: warm private copy", () => template.Copy(merge, ceiling));
            using (warm)
            {
                Assert.Equal(0, warmCounts.Total);
                Assert.NotEqual(copy.Repository, warm.Repository);
                Assert.Equal(copy.Candidate, warm.Candidate);
                var (_, product) = Measure("product: integration-only proof", () => Prove(warm, out _));
                Assert.True(product.Git.Total.Launches > 0);
            }
        }
    }

    [Theory]
    [InlineData("writer", 20)]
    [InlineData("writer", 19)]
    [InlineData("writer", 17)]
    [InlineData("goal-edit", 20)]
    [InlineData("raise", 20)]
    [InlineData("foreign", 20)]
    [InlineData("dispatch", 20)]
    public void ClassSetup_ReadsBackWithRealGit(string merge, int ceiling)
    {
        using var template = new PatchEquivalenceAutoRebindRepositoryTemplate();
        using var copy = template.Copy(merge, ceiling);
        var root = copy.Repository;
        var seed = Git(root, "rev-parse", $"{copy.Bound}~2");
        var content = merge == "goal-edit" ? "edited goal line\n" : "goal owned line\n";
        AssertTree(root, copy.Bound, seed, Entry(GuardedPath, GuardedLines(6, 15)),
            Entry(GoalPath, "goal owned line\n"), Entry(SourceSizeRatchet.SourcePath, Authority(ceiling)));
        AssertTree(root, copy.Main, seed, Entry(GuardedPath, GuardedLines(1, 20) + MainGrowth),
            Entry(SourceSizeRatchet.SourcePath, Authority(20)), Entry("src/unrelated.txt", "second main advance\n"));
        AssertTree(root, copy.Candidate, seed, Entry(GuardedPath, GuardedLines(6, 15) + MainGrowth),
            Entry(GoalPath, content), Entry(SourceSizeRatchet.SourcePath, Authority(merge == "raise" ? 25 : 18)),
            Entry("src/unrelated.txt", "second main advance\n"));
        Assert.Equal("Tests <tests@example.invalid>", Git(root, "log", "-1", "--format=%an <%ae>", copy.Bound));
        AssertWorktree(root, copy.Worktree, copy.Goal.Id, copy.Candidate);
        AssertMain(root, copy.Main);
    }
}
