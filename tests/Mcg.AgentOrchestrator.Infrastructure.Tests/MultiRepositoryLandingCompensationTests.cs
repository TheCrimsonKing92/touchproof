using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: real repositories and state are private to each case.
public sealed class MultiRepositoryLandingCompensationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AdvanceOnce_HomeLandingFails_RevertsSecondaryAndPersistsHold(bool throws)
    {
        using var fixture = new MultiRepositoryAcceptanceGateFixture();
        var crosswalk = fixture.Repositories.Crosswalk;
        var before = crosswalk.Git("rev-parse", "main");
        fixture.HomeLanding = () => throws ? throw new IOException("fixture landing failed")
            : new(fixture.Goal.Id.Value, fixture.Goal.Id.Value[..8], new LandingDecision.Escalate("fixture landing failed"),
                "main", false, "fixture landing failed");
        var driver = fixture.Driver();

        var hold = Assert.IsType<ConductorAdvanceOutcome.Held>(driver.AdvanceOnce(fixture.Goal,
            ConductorAutonomyPolicy.Conservative).Outcome);

        Assert.Equal(MultiRepositoryLandingFailedHold.Kind, hold.Decision!.DiscriminatingEvidence);
        var failure = new MultiRepositoryLandingRecordStore(fixture.Repositories.Workspace.OrchestratorDirectory).ReadFailure(fixture.Goal.Id)!;
        Assert.Equal("default", failure.FailingKey);
        Assert.Contains("fixture landing failed", failure.Error);
        var revert = Assert.Single(failure.CompensatingCommits["crosswalk"]);
        Assert.Equal(revert, crosswalk.Git("rev-parse", "main"));
        Assert.NotEqual(before, revert);
        Assert.Equal(crosswalk.Git("rev-parse", before + "^{tree}"), crosswalk.Git("rev-parse", "main^{tree}"));
        Assert.Contains("Revert", crosswalk.Git("log", "-1", "--format=%s"));
        Assert.Contains(revert, hold.Reason);
        Assert.Contains(hold.Decision.Facts, fact => fact.Name == "owner" && fact.Value == "operator");
        Assert.Empty(failure.ManualRepairKeys);
        Assert.Null(fixture.Gate.Store.TryRead(fixture.Goal.Id));
        Assert.Equal(1, fixture.LandingCalls);

        var again = Assert.IsType<ConductorAdvanceOutcome.Held>(fixture.Driver().AdvanceOnce(fixture.Goal,
            ConductorAutonomyPolicy.Conservative).Outcome);
        Assert.Equal(hold.Reason, again.Reason);
        Assert.Equal(2, fixture.Calls.Count);
        Assert.Equal(1, fixture.LandingCalls);
        Assert.Equal(revert, crosswalk.Git("rev-parse", "main"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AdvanceOnce_FailureAfterHomeMutation_RepairsOrNamesManualRepair(bool conflict)
    {
        using var fixture = new MultiRepositoryAcceptanceGateFixture();
        var home = fixture.Repositories.Home;
        var secondaryBefore = fixture.Repositories.Crosswalk.Git("rev-parse", "main^{tree}");
        var homeBefore = home.Git("rev-parse", "main^{tree}");
        fixture.HomeLanding = () =>
        {
            var landed = LandingExecutor.Execute(fixture.Repositories.Kernel, fixture.Goal, fixture.Repositories.Workspace);
            Assert.True(landed.MainAdvanced, landed.Message);
            GoalOperationJournal.Completed(home.CheckoutPath, fixture.Goal, "conductor:land");
            if (conflict) GoalRepositoryWorktreeProvisionerFixture.CommitChange(home.CheckoutPath, "later unrelated edit\n");
            throw new IOException("post-landing failure");
        };

        var hold = Assert.IsType<ConductorAdvanceOutcome.Held>(fixture.Driver().AdvanceOnce(fixture.Goal,
            ConductorAutonomyPolicy.Conservative).Outcome);

        var failure = new MultiRepositoryLandingRecordStore(fixture.Repositories.Workspace.OrchestratorDirectory).ReadFailure(fixture.Goal.Id)!;
        Assert.Equal(MultiRepositoryLandingFailedHold.Kind, hold.Decision!.DiscriminatingEvidence);
        Assert.Equal(secondaryBefore, fixture.Repositories.Crosswalk.Git("rev-parse", "main^{tree}"));
        Assert.Single(failure.CompensatingCommits["crosswalk"]); // Continue even if the newer home repair fails.
        if (conflict)
        {
            Assert.Equal(new[] { "default" }, failure.ManualRepairKeys);
            Assert.Equal("later unrelated edit", home.Git("show", "main:example.txt"));
            Assert.Contains("Manual repair required: default", hold.Reason);
        }
        else
        {
            Assert.Empty(failure.ManualRepairKeys);
            Assert.Single(failure.CompensatingCommits["default"]);
            Assert.Equal(homeBefore, home.Git("rev-parse", "main^{tree}"));
        }
        var again = Assert.IsType<ConductorAdvanceOutcome.Held>(fixture.Driver().AdvanceOnce(fixture.Goal,
            ConductorAutonomyPolicy.Conservative).Outcome);
        Assert.Equal(MultiRepositoryLandingFailedHold.Kind, again.Decision!.DiscriminatingEvidence);
        Assert.Equal(GoalStatus.Verified, fixture.Goal.Status);
    }

    [Fact]
    public void AdvanceOnce_LaterUnrelatedCommit_CompensationPreservesItsHistory()
    {
        using var fixture = new MultiRepositoryAcceptanceGateFixture();
        var crosswalk = fixture.Repositories.Crosswalk;
        string? unrelated = null;
        fixture.HomeLanding = () =>
        {
            File.WriteAllText(Path.Combine(crosswalk.CheckoutPath, "unrelated.txt"), "preserve me\n");
            crosswalk.Git("add", "unrelated.txt");
            crosswalk.Git("commit", "-m", "Unrelated later change");
            unrelated = crosswalk.Git("rev-parse", "main");
            throw new IOException("home failed");
        };
        var hold = Assert.IsType<ConductorAdvanceOutcome.Held>(fixture.Driver().AdvanceOnce(fixture.Goal,
            ConductorAutonomyPolicy.Conservative).Outcome);
        Assert.NotNull(unrelated);
        Assert.Equal(MultiRepositoryLandingFailedHold.Kind, hold.Decision!.DiscriminatingEvidence);
        Assert.Equal("preserve me", crosswalk.Git("show", "main:unrelated.txt"));
        Assert.Equal("seed", crosswalk.Git("show", "main:example.txt"));
        crosswalk.Git("merge-base", "--is-ancestor", unrelated!, "main");
    }

    [Fact]
    public void AdvanceOnce_RevertCheckoutSyncFails_RecordsPublishedRepairCommit()
    {
        using var fixture = new MultiRepositoryAcceptanceGateFixture();
        var crosswalk = fixture.Repositories.Crosswalk;
        var beforeTree = crosswalk.Git("rev-parse", "main^{tree}");
        fixture.HomeLanding = () => throw new IOException("home failed");
        var syncs = 0;
        using var git = LandingExecutor.UseGitRunner((root, args) =>
        {
            if (root == crosswalk.CheckoutPath && args[0] == "read-tree" && ++syncs == 2)
                return new GitCli.GitResult(1, "", "fixture repair checkout sync failed");
            return GitCli.Run(root, args);
        });

        var hold = Assert.IsType<ConductorAdvanceOutcome.Held>(fixture.Driver().AdvanceOnce(fixture.Goal,
            ConductorAutonomyPolicy.Conservative).Outcome);

        var failure = new MultiRepositoryLandingRecordStore(fixture.Repositories.Workspace.OrchestratorDirectory).ReadFailure(fixture.Goal.Id)!;
        Assert.Equal(2, syncs);
        var commit = Assert.Single(failure.CompensatingCommits["crosswalk"]);
        Assert.Equal(commit, crosswalk.Git("rev-parse", "main"));
        Assert.Equal(beforeTree, crosswalk.Git("rev-parse", "main^{tree}"));
        Assert.Equal(new[] { "crosswalk" }, failure.ManualRepairKeys);
        Assert.Contains(commit, hold.Reason);
        Assert.Contains("fixture repair checkout sync failed", hold.Reason);
    }
}
