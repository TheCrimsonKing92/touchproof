using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each case owns its real repositories, registry and stores; git overrides are execution-scoped.
public sealed class MultiRepositoryHomeLandingPreflightTests
{
    [Fact]
    public void Execute_HomePendingHumanWait_HoldsBeforeAnyRepositoryLands()
    {
        using var fixture = new MultiRepositoryAcceptanceGateFixture();
        var receipt = PassingReceipt(fixture);
        var kernel = fixture.Repositories.Kernel;
        var wait = kernel.RequestHumanInput(fixture.Goal.Id, null, "fixture home wait");

        AssertBlocked(fixture, kernel, fixture.Goal, receipt, wait.Id.Value);
    }

    [Fact]
    public void Execute_HomePendingGateTestObligation_HoldsBeforeAnyRepositoryLands()
    {
        using var fixture = new MultiRepositoryAcceptanceGateFixture();
        var receipt = PassingReceipt(fixture);
        var kernel = GateTestObligationIntakeTests.Seed(fixture.Repositories.Kernel, fixture.Goal, ["MissingHomeTests"]);
        var goal = kernel.GetGoal(fixture.Goal.Id);
        Assert.NotNull(goal.Repositories);
        Assert.Equal(fixture.Goal.Repositories!.DeclaredOrder, goal.Repositories.DeclaredOrder);
        var obligation = Assert.Single(goal.GateTestObligations);

        AssertBlocked(fixture, kernel, goal, receipt, "gate test obligation " + obligation.Id[..8]);
    }

    [Fact]
    public void Execute_HomeAdmissible_LandsDeclaredOrderWithHomeLast()
    {
        using var fixture = new MultiRepositoryAcceptanceGateFixture();
        var receipt = PassingReceipt(fixture);
        var home = fixture.Repositories.Home;
        var crosswalk = fixture.Repositories.Crosswalk;
        var order = new List<string>();
        using var git = LandingExecutor.UseGitRunner((root, args) =>
        {
            var result = GitCli.Run(root, args);
            if (result.Succeeded && args.Length >= 2 && args[0] == "update-ref" && args[1] == "refs/heads/main")
                order.Add(root == home.CheckoutPath ? "default" : "crosswalk");
            return result;
        });
        var landing = Landing(fixture);
        var preflights = 0;

        var result = landing.Execute(fixture.Goal, GoalLifecycleState.Verified, receipt,
            () => LandingExecutor.Execute(fixture.Repositories.Kernel, fixture.Goal, fixture.Repositories.Workspace),
            () => null, () =>
            {
                preflights++;
                Assert.Empty(order);
                var refusal = HomeLandingAdmissibility.Evaluate(fixture.Repositories.Kernel, fixture.Goal, fixture.Repositories.Workspace);
                Assert.Null(refusal);
                return refusal;
            });

        Assert.Equal(1, preflights);
        Assert.Null(result.Hold);
        Assert.NotNull(result.Record);
        Assert.True(result.HomeLanding!.MainAdvanced, result.HomeLanding.Message);
        Assert.Equal(new[] { "crosswalk", "default" }, order);
        Assert.Equal(order, result.Record.Order);
        Assert.Equal("fixture secondary change", crosswalk.Git("show", "main:example.txt"));
        Assert.Equal("fixture home change", home.Git("show", "main:example.txt"));
        Assert.Equal(crosswalk.Git("rev-parse", "main"), result.Record.LandedCommits["crosswalk"]);
        Assert.Equal(home.Git("rev-parse", "main"), result.Record.LandedCommits["default"]);
        Assert.NotNull(landing.Store.ReadLanding(fixture.Goal.Id));
        Assert.Null(landing.Store.ReadFailure(fixture.Goal.Id));
    }

    [Fact]
    public void Execute_PreflightPassesThenMutationBlockerRefuses_CompensatesLandedRepositories()
    {
        using var fixture = new MultiRepositoryAcceptanceGateFixture();
        var receipt = PassingReceipt(fixture);
        var homeBefore = fixture.Repositories.Home.Git("rev-parse", "main");
        var secondaryBefore = fixture.Repositories.Crosswalk.Git("rev-parse", "main");
        var landing = Landing(fixture);
        var preflights = 0;
        var mutations = 0;
        var homeCalls = 0;

        var result = landing.Execute(fixture.Goal, GoalLifecycleState.Verified, receipt,
            () => { homeCalls++; throw new InvalidOperationException("Home must not be called after mutation refusal."); },
            () =>
            {
                Assert.Equal(1, preflights);
                if (++mutations == 1) return null;
                Assert.NotEqual(secondaryBefore, fixture.Repositories.Crosswalk.Git("rev-parse", "main"));
                return "fixture later mutation blocker";
            }, () =>
            {
                preflights++;
                Assert.Null(HomeLandingAdmissibility.Evaluate(fixture.Repositories.Kernel, fixture.Goal, fixture.Repositories.Workspace));
                return null;
            });

        Assert.Equal(1, preflights);
        Assert.Equal(2, mutations);
        Assert.Equal(0, homeCalls);
        AssertCompensated(fixture, landing, result, homeBefore, secondaryBefore, "fixture later mutation blocker");
    }

    [Fact]
    public void Execute_PreflightPassesThenHomeLandingRefuses_CompensatesLandedRepositories()
    {
        using var fixture = new MultiRepositoryAcceptanceGateFixture();
        var receipt = PassingReceipt(fixture);
        var homeBefore = fixture.Repositories.Home.Git("rev-parse", "main");
        var secondaryBefore = fixture.Repositories.Crosswalk.Git("rev-parse", "main");
        var landing = Landing(fixture);
        var kernel = fixture.Repositories.Kernel;
        var preflights = 0;
        LandingResult? homeResult = null;

        var result = landing.Execute(fixture.Goal, GoalLifecycleState.Verified, receipt,
            () =>
            {
                Assert.NotEqual(secondaryBefore, fixture.Repositories.Crosswalk.Git("rev-parse", "main"));
                homeResult = LandingExecutor.Execute(kernel, fixture.Goal, fixture.Repositories.Workspace);
                return homeResult;
            }, () => null, () =>
            {
                preflights++;
                Assert.Null(HomeLandingAdmissibility.Evaluate(kernel, fixture.Goal, fixture.Repositories.Workspace));
                kernel.RequestHumanInput(fixture.Goal.Id, null, "fixture wait after preflight");
                return null;
            });

        Assert.Equal(1, preflights);
        Assert.NotNull(homeResult);
        Assert.False(homeResult.MainAdvanced);
        AssertCompensated(fixture, landing, result, homeBefore, secondaryBefore, homeResult.Message);
    }

    [Fact]
    public void Execute_PreflightThrows_HoldsWithoutLandingRecords()
    {
        using var fixture = new MultiRepositoryAcceptanceGateFixture();
        var receipt = PassingReceipt(fixture);
        AssertBlocked(fixture, fixture.Repositories.Kernel, fixture.Goal, receipt, "IOException: fixture preflight failure",
            () => throw new IOException("fixture preflight failure"));
    }

    [Fact]
    public void Evaluate_HomeRevisionUnresolved_ReturnsRefusal()
    {
        using var fixture = new MultiRepositoryAcceptanceGateFixture();
        using var git = LandingExecutor.UseGitRunner((root, args) =>
        {
            Assert.Equal(fixture.Repositories.Workspace.ExecutionDirectory, root);
            Assert.Equal(new[] { "rev-parse", "refs/heads/" + GoalWorktrees.BranchName(fixture.Goal.Id) }, args);
            return new GitCli.GitResult(1, "", "fixture revision unavailable");
        });

        Assert.Equal("home candidate revision unresolved: fixture revision unavailable",
            HomeLandingAdmissibility.Evaluate(fixture.Repositories.Kernel, fixture.Goal, fixture.Repositories.Workspace));
    }

    private static MultiRepositoryAcceptanceReceipt PassingReceipt(MultiRepositoryAcceptanceGateFixture fixture)
    {
        var receipt = fixture.Gate.Run(fixture.Goal, GoalLifecycleState.Verified, fixture.RunHome).Landable;
        Assert.NotNull(receipt);
        Assert.True(receipt.Passed);
        return receipt;
    }

    private static MultiRepositoryTupleLanding Landing(MultiRepositoryAcceptanceGateFixture fixture) =>
        new(fixture.Repositories.Workspace, fixture.Repositories.Registry);

    private static void AssertBlocked(MultiRepositoryAcceptanceGateFixture fixture, AgentOrchestratorKernel kernel,
        Goal goal, MultiRepositoryAcceptanceReceipt receipt, string detail, Func<string?>? preflight = null)
    {
        var homeBefore = fixture.Repositories.Home.Git("rev-parse", "main");
        var secondaryBefore = fixture.Repositories.Crosswalk.Git("rev-parse", "main");
        var landing = Landing(fixture);
        var preflights = 0;
        var mutations = 0;
        var homeCalls = 0;

        var result = landing.Execute(goal, GoalLifecycleState.Verified, receipt,
            () => { homeCalls++; throw new InvalidOperationException("Blocked preflight must not call home landing."); },
            () => { mutations++; return null; }, () =>
            {
                preflights++;
                return preflight is not null ? preflight() : HomeLandingAdmissibility.Evaluate(kernel, goal, fixture.Repositories.Workspace);
            });

        Assert.Equal(1, preflights);
        Assert.Equal(0, mutations);
        Assert.Equal(0, homeCalls);
        var hold = Assert.IsType<ConductorAdvanceOutcome.Held>(result.Hold);
        Assert.Equal("multi-repository-landing-blocked-by-home", hold.Decision!.DiscriminatingEvidence);
        Assert.Contains(detail, hold.Reason);
        Assert.Contains(hold.Decision.Facts, fact => fact.Name == "blocker" && fact.Value.Contains(detail));
        Assert.Equal(homeBefore, fixture.Repositories.Home.Git("rev-parse", "main"));
        Assert.Equal(secondaryBefore, fixture.Repositories.Crosswalk.Git("rev-parse", "main"));
        Assert.Null(result.Record);
        Assert.Null(landing.Store.ReadAttempt(goal.Id));
        Assert.Null(landing.Store.ReadFailure(goal.Id));
        Assert.Null(landing.Store.ReadLanding(goal.Id));
        Assert.Equal(receipt.TupleCanonical, fixture.Gate.Store.TryRead(goal.Id)!.TupleCanonical);
    }

    private static void AssertCompensated(MultiRepositoryAcceptanceGateFixture fixture, MultiRepositoryTupleLanding landing,
        MultiRepositoryTupleLandingResult result, string homeBefore, string secondaryBefore, string error)
    {
        Assert.Equal(MultiRepositoryLandingFailedHold.Kind, result.Hold!.Decision!.DiscriminatingEvidence);
        var failure = landing.Store.ReadFailure(fixture.Goal.Id);
        Assert.NotNull(failure);
        Assert.Equal("default", failure.FailingKey);
        Assert.Contains(error, failure.Error);
        var revert = Assert.Single(failure.CompensatingCommits["crosswalk"]);
        Assert.Equal(revert, fixture.Repositories.Crosswalk.Git("rev-parse", "main"));
        Assert.NotEqual(secondaryBefore, revert);
        Assert.Equal(fixture.Repositories.Crosswalk.Git("rev-parse", secondaryBefore + "^{tree}"),
            fixture.Repositories.Crosswalk.Git("rev-parse", "main^{tree}"));
        Assert.Equal(homeBefore, fixture.Repositories.Home.Git("rev-parse", "main"));
        Assert.Empty(failure.ManualRepairKeys);
        Assert.Null(landing.Store.ReadLanding(fixture.Goal.Id));
        Assert.Null(fixture.Gate.Store.TryRead(fixture.Goal.Id));
    }
}
