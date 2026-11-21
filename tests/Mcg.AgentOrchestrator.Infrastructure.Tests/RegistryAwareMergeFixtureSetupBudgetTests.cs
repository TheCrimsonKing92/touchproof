using System.Text;
using static AdditiveConflictGitFixture;
using static FixtureGitLaunchBudget;

public sealed class RegistryAwareMergeFixtureSetupBudgetTests : GoalWorktreeTestBase
{
    [Fact]
    public void ClassSetup_StaysWithinRealGitLaunchBudget()
    {
        // Cold seed preparation can launch init, two configs, add, commit and branch.
        var (seed, seedLaunches) = Measure("setup: seed template", () => CreateReducedAcceptanceCohortRepository());
        DeleteDirectory(seed);
        Assert.InRange(seedLaunches.Total, 0, 6);

        var (fixture, setup) = Measure("setup: registry and source commits",
            () => GoalWorktreeTestsRebaseRegistryAwareMerge.AdditionsAndRemovalFixture(inProcessSetup: true));
        using (fixture)
        {
            // Worktree creation can launch worktree add and the revision reader. The
            // base, goal and main fixture commits are handled by the writer.
            Assert.Empty(setup.Probes);
            Assert.InRange(setup.Total, 1, 3);
            var (_, product) = Measure("product: registry-aware rebase",
                () => GoalWorktreeTestsRebaseRegistryAwareMerge.Rebase(fixture));
            Assert.True(product.Git.Total.Launches > 0);
        }

        var (addAdd, addAddSetup) = Measure("setup: empty base and BOM/CRLF",
            () => new AdditiveConflictGitFixture([new(WidgetPath, null, MainText, GoalText)],
                bom: true, lineEnding: "\r\n", inProcessSetup: true));
        using (addAdd)
        {
            Assert.Empty(addAddSetup.Probes);
            Assert.InRange(addAddSetup.Total, 1, 3);
        }
    }

    [Theory]
    [InlineData(false, "\n")]
    [InlineData(true, "\r\n")]
    public void ClassSetup_ReadsBackWithRealGit(bool bom, string lineEnding)
    {
        using var fixture = new AdditiveConflictGitFixture(
            [new(WidgetPath, BaseText, MainText, GoalText)],
            bom: bom, lineEnding: lineEnding, inProcessSetup: true);
        var root = fixture.Repository;
        var parent = FixtureGitLaunchBudget.Git(root, "rev-parse", $"{fixture.MainHead}^");
        var seed = FixtureGitLaunchBudget.Git(root, "rev-parse", $"{parent}^");
        Assert.Equal(parent, FixtureGitLaunchBudget.Git(root, "rev-parse", $"{fixture.OriginalHead}^"));
        AssertTree(root, parent, seed, (WidgetPath, Bytes(BaseText)));
        AssertTree(root, fixture.MainHead, seed, (WidgetPath, Bytes(MainText)));
        AssertTree(root, fixture.OriginalHead, seed, (WidgetPath, Bytes(GoalText)));
        Assert.Equal("Worktree Tests <tests@example.com>",
            FixtureGitLaunchBudget.Git(fixture.Worktree, "log", "-1", "--format=%an <%ae>"));
        AssertWorktree(root, fixture.Worktree, fixture.GoalId, fixture.OriginalHead);
        AssertMain(root, fixture.MainHead);

        byte[] Bytes(string content)
        {
            var encoding = new UTF8Encoding(bom);
            return [.. encoding.GetPreamble(), .. encoding.GetBytes(content.ReplaceLineEndings(lineEnding))];
        }
    }
}
