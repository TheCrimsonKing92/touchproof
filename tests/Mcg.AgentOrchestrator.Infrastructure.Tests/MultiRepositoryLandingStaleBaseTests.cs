using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: all repositories, registry and receipts are case-owned.
public sealed class MultiRepositoryLandingStaleBaseTests
{
    [Theory]
    [InlineData("crosswalk")]
    [InlineData("default")]
    public void AdvanceOnce_IntegrationMoved_LandsNothingAndRegatesWholeTuple(string key)
    {
        using var fixture = new MultiRepositoryAcceptanceGateFixture();
        var receipt = fixture.Gate.Run(fixture.Goal, GoalLifecycleState.Verified, fixture.RunHome).Landable!;
        var moved = key == "default" ? fixture.Repositories.Home : fixture.Repositories.Crosswalk;
        File.WriteAllText(Path.Combine(moved.CheckoutPath, "unrelated.txt"), "other goal\n");
        moved.Git("add", "unrelated.txt");
        moved.Git("commit", "-m", "Advance integration after acceptance");
        Assert.NotEqual(receipt.IntegrationBases[key], moved.Git("rev-parse", "main"));
        var homeBefore = fixture.Repositories.Home.Git("rev-parse", "main");
        var otherBefore = fixture.Repositories.Crosswalk.Git("rev-parse", "main");
        var driver = fixture.Driver();

        var hold = Assert.IsType<ConductorAdvanceOutcome.Held>(driver.AdvanceOnce(fixture.Goal,
            ConductorAutonomyPolicy.Conservative).Outcome);

        Assert.Equal("multi-repository-acceptance-stale", hold.Decision!.DiscriminatingEvidence);
        Assert.Contains(key, hold.Reason);
        Assert.Contains("re-gate every declared repository", hold.Reason);
        Assert.Equal(homeBefore, fixture.Repositories.Home.Git("rev-parse", "main"));
        Assert.Equal(otherBefore, fixture.Repositories.Crosswalk.Git("rev-parse", "main"));
        Assert.Equal(0, fixture.LandingCalls);
        Assert.Equal(2, fixture.Calls.Count);
        Assert.Null(fixture.Gate.Store.TryRead(fixture.Goal.Id));
        Assert.False(fixture.Gate.HasCurrentPass(fixture.Goal));

        Assert.IsType<ConductorAdvanceOutcome.Executed>(driver.AdvanceOnce(fixture.Goal,
            ConductorAutonomyPolicy.Conservative).Outcome);
        Assert.Equal(new[] { "default", "crosswalk", "default", "crosswalk" }, fixture.Calls.Select(call => call.Key));
        var refreshed = fixture.Gate.Store.TryRead(fixture.Goal.Id)!;
        Assert.Equal(key == "default" ? homeBefore : otherBefore, refreshed.IntegrationBases[key]);
    }

    [Fact]
    public void AdvanceOnce_LegacyReceiptWithoutBases_RequiresFreshGate()
    {
        using var fixture = new MultiRepositoryAcceptanceGateFixture();
        var receipt = fixture.Gate.Run(fixture.Goal, GoalLifecycleState.Verified, fixture.RunHome).Landable!;
        fixture.Gate.Store.Write(new(receipt.GoalId, receipt.TupleCanonical, receipt.Outcomes, receipt.RecordedAt), fixture.Goal.Repositories!.Repositories);
        var hold = Assert.IsType<ConductorAdvanceOutcome.Held>(fixture.Driver().AdvanceOnce(fixture.Goal,
            ConductorAutonomyPolicy.Conservative).Outcome);
        Assert.Equal("multi-repository-acceptance-stale", hold.Decision!.DiscriminatingEvidence);
        Assert.Contains("recorded=missing", hold.Reason);
        Assert.Equal(0, fixture.LandingCalls);
        Assert.Null(fixture.Gate.Store.TryRead(fixture.Goal.Id));
    }

    [Fact]
    public void AdvanceOnce_BaseMovesBetweenLandings_CompensatesEarlierLanding()
    {
        using var fixture = new MultiRepositoryAcceptanceGateFixture();
        var home = fixture.Repositories.Home;
        var other = fixture.Repositories.Crosswalk;
        var beforeTree = other.Git("rev-parse", "main^{tree}");
        string? moved = null;
        using var git = LandingExecutor.UseGitRunner((root, args) =>
        {
            var result = GitCli.Run(root, args);
            if (moved is null && result.Succeeded && root == other.CheckoutPath && args.Length >= 2
                && args[0] == "update-ref" && args[1] == "refs/heads/main")
            {
                File.WriteAllText(Path.Combine(home.CheckoutPath, "unrelated.txt"), "other landing\n");
                home.Git("add", "unrelated.txt");
                home.Git("commit", "-m", "Move home after tuple preflight");
                moved = home.Git("rev-parse", "main");
            }
            return result;
        });
        var hold = Assert.IsType<ConductorAdvanceOutcome.Held>(fixture.Driver().AdvanceOnce(fixture.Goal,
            ConductorAutonomyPolicy.Conservative).Outcome);
        Assert.NotNull(moved);
        Assert.Equal(MultiRepositoryLandingFailedHold.Kind, hold.Decision!.DiscriminatingEvidence);
        Assert.Contains("default: integration branch moved after preflight", hold.Reason);
        Assert.Equal(moved, home.Git("rev-parse", "main"));
        Assert.Equal(beforeTree, other.Git("rev-parse", "main^{tree}"));
        Assert.Equal(0, fixture.LandingCalls);
    }
}
