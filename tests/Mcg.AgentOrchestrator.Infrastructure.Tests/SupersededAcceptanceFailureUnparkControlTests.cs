using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using static AttributedFailureDeveloperRouteTests;

// Parallel-safe: all persistence, revision inputs, gates and workers are fixture-owned.
public sealed class SupersededAcceptanceFailureUnparkControlTests
{
    [Theory(Timeout = TestHangGuard.WholeTestMilliseconds)]
    [Trait("Category", "CrossTick")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Verified_HistoricalUnboundFailure_RunsRequiredGate(bool freshRed)
    {
        using var f = new SupersededAcceptanceFailureUnparkFixture();
        f.Kernel.RecordAcceptanceFailure(f.Goal.Id, ["previous verifier failure"]);
        await f.Restart();
        await f.RefreshMetadata();
        Assert.Equal(GoalStatus.Verified, f.Goal.Status);
        var historical = f.Goal.LatestAcceptanceFailure;
        Assert.NotNull(historical);
        Assert.Null(historical.BranchHeadSha);
        Assert.Null(historical.MainHeadSha);
        Assert.Null(historical.AttemptId);
        Assert.Null(historical.SoloFailureReceipt);
        var tasks = f.Tasks();
        var fresh = freshRed ? await f.PrepareFreshRed() : null;

        var result = f.Tick(f.Driver());

        Assert.Equal(new(f.Current.CandidateSha, f.Current.MainSha), Assert.Single(f.Gates));
        Assert.Equal(tasks, f.Tasks());
        Assert.Empty(f.Starts);
        Assert.Empty(f.Supersessions());
        Assert.Equal(0, f.Goal.AutomaticAcceptanceRetryCount);
        if (fresh is null)
        {
            Assert.Contains("Landed:", Assert.IsType<ConductorAdvanceOutcome.Executed>(result.Outcome).Description);
            Assert.Empty(f.Holds);
        }
        else
        {
            Assert.IsType<ConductorAdvanceOutcome.Held>(result.Outcome);
            Assert.Equal(GoalStatus.AcceptanceFailed, f.Goal.Status);
            var failure = f.Goal.LatestAcceptanceFailure!;
            Assert.Equal(fresh.AttemptId, failure.AttemptId);
            Assert.Equal(f.Current.CandidateSha, failure.BranchHeadSha);
            Assert.Equal(f.Current.MainSha, failure.MainHeadSha);
            Assert.NotEqual(historical, failure);
        }
    }

    [Fact(Timeout = TestHangGuard.WholeTestMilliseconds)]
    [Trait("Category", "CrossTick")]
    public async Task Unpark_CurrentPair_RoutesDeveloperAndChargesOnce()
    {
        using var f = new SupersededAcceptanceFailureUnparkFixture();
        await f.RecordRed();
        await f.ParkAdvanceUnpark(advance: false, recoverVerified: false);
        await f.Restart();
        var driver = f.Driver();
        f.Tick(driver);
        f.Tick(driver);
        var developer = f.Goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
        Assert.Equal(developer.Id, Assert.Single(f.Starts));
        Assert.Equal(developer.Id, Assert.Single(MarkerRetries(f.Goal)).TaskId);
        Assert.Equal(1, f.Goal.AutomaticAcceptanceRetryCount);
        Assert.Empty(f.Gates);
        Assert.Empty(f.Supersessions());
    }

    [Fact(Timeout = TestHangGuard.WholeTestMilliseconds)]
    [Trait("Category", "CrossTick")]
    public async Task FreshGate_CurrentPairRed_RoutesNewFailureToDeveloper()
    {
        using var f = new SupersededAcceptanceFailureUnparkFixture();
        await f.RecordRed();
        await f.ParkAdvanceUnpark(advance: true);
        var driver = f.Driver();
        f.Tick(driver);
        Assert.Empty(f.Gates);
        Assert.Equal(0, f.Goal.AutomaticAcceptanceRetryCount);
        var fresh = await f.PrepareFreshRed();
        Assert.NotEqual(f.Original.AttemptId, fresh.AttemptId);
        f.Tick(driver);
        Assert.Single(f.Gates);
        var failure = Assert.IsType<AcceptanceFailureSummary>(f.Goal.LatestAcceptanceFailure);
        Assert.Equal(f.Current.CandidateSha, failure.BranchHeadSha);
        Assert.Equal(f.Current.MainSha, failure.MainHeadSha);
        Assert.Equal(fresh.AttemptId, failure.AttemptId);
        Assert.Equal(fresh.FailedChecks, failure.FailedChecks);
        Assert.Equal(0, f.Goal.AutomaticAcceptanceRetryCount);
        Assert.Empty(f.Starts);
        f.Tick(driver);
        Assert.Equal(1, f.Goal.AutomaticAcceptanceRetryCount);
        var retry = Assert.Single(MarkerRetries(f.Goal));
        Assert.Equal(f.Goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer).Id, retry.TaskId);
        Assert.Contains("receipt_id=" + fresh.AttemptId, retry.Message);
        Assert.Equal(fresh.AttemptId, f.Goal.RetainedAcceptanceFailure!.AttemptId);
        Assert.NotEmpty(fresh.RequiredUnmetCriteria);
        Assert.All(fresh.RequiredUnmetCriteria, check =>
        {
            Assert.False(check.Passed);
            Assert.True(check.ExecutedTestCount > 0);
            Assert.Equal(fresh.AttemptId, check.TestResultAttemptId);
        });
        Assert.Equal(retry.TaskId, Assert.Single(f.Starts));
        Assert.Single(f.Gates);
        f.AssertHistory();
    }

    [Theory(Timeout = TestHangGuard.WholeTestMilliseconds)]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Completion_UnknownPair_HoldsWithoutAcceptanceFailedTransition(bool recorded)
    {
        using var f = new SupersededAcceptanceFailureUnparkFixture();
        await f.RecordRed();
        Assert.True(f.Kernel.ReconcileGoalVerificationStatus(f.Goal.Id, "Recover passing source review"));
        if (!recorded) f.Current = f.Current with { MainSha = "" };
        var summary = recorded ? f.Original with { BranchHeadSha = null } : f.Original;
        var tasks = f.Tasks();
        var held = Assert.IsType<ConductorAdvanceOutcome.Held>(f.Complete(summary).Outcome);
        Assert.Equal(AcceptanceFailurePairCurrency.Unknown, held.Decision!.DiscriminatingEvidence);
        Assert.Equal(GoalStatus.Verified, f.Goal.Status);
        Assert.Equal(summary.AttemptId, f.Goal.LatestAcceptanceFailure!.AttemptId);
        Assert.Equal(tasks, f.Tasks());
        Assert.Equal(0, f.Goal.AutomaticAcceptanceRetryCount);
        Assert.Empty(f.Starts);
        Assert.Empty(f.Gates);
        Assert.Empty(f.Supersessions());
    }

    [Theory(Timeout = TestHangGuard.WholeTestMilliseconds)]
    [Trait("Category", "CrossTick")]
    [InlineData("recorded-branch", GoalStatus.AcceptanceFailed)]
    [InlineData("recorded-main", GoalStatus.Verified)]
    [InlineData("recorded-pair", GoalStatus.Verified)]
    [InlineData("current-branch", GoalStatus.Verified)]
    [InlineData("current-main", GoalStatus.Active)]
    public async Task MissingRevision_HoldsAndRetainsFailure(string missing, GoalStatus status)
    {
        using var f = new SupersededAcceptanceFailureUnparkFixture();
        await f.RecordRed();
        await f.ParkAdvanceUnpark(advance: true, recoverVerified: status != GoalStatus.AcceptanceFailed);
        if (missing.StartsWith("recorded", StringComparison.Ordinal))
            f.Kernel.RecordAcceptanceFailure(f.Goal.Id, f.Original.FailedChecks!,
                missing is "recorded-branch" or "recorded-pair" ? null : f.Original.BranchHeadSha,
                missing is "recorded-main" or "recorded-pair" ? null : f.Original.MainHeadSha,
                attemptId: f.Original.AttemptId, soloFailureReceipt: f.Original.SoloFailureReceipt);
        if (missing == "current-branch") f.Current = f.Current with { CandidateSha = " " };
        if (missing == "current-main") f.Current = f.Current with { MainSha = "" };
        if (status == GoalStatus.Active) f.Goal.SetStatus(GoalStatus.Active);
        await f.Restart();
        var before = f.Goal.LatestAcceptanceFailure;
        var tasks = f.Tasks();
        var driver = f.Driver();
        for (var tick = 0; tick < 2; tick++)
        {
            var held = Assert.IsType<ConductorAdvanceOutcome.Held>(f.Tick(driver).Outcome);
            Assert.Equal(AcceptanceFailurePairCurrency.Unknown, held.Decision!.DiscriminatingEvidence);
            Assert.Contains("missing revisions=", held.Reason);
        }
        Assert.NotEmpty(f.Holds);
        Assert.Same(before, f.Goal.LatestAcceptanceFailure);
        Assert.Equal(tasks, f.Tasks());
        Assert.Equal(0, f.Goal.AutomaticAcceptanceRetryCount);
        Assert.Empty(f.Supersessions());
        Assert.Empty(f.Starts);
        Assert.Empty(f.Gates);
    }
}
