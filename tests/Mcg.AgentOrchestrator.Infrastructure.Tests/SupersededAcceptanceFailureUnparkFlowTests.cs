using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;

// Parallel-safe: each case owns its repository, database, workers and gate stub.
public sealed class SupersededAcceptanceFailureUnparkFlowTests
{
    [Theory(Timeout = TestHangGuard.WholeTestMilliseconds)]
    [Trait("Category", "CrossTick")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unpark_ChangedPair_AdmitsOneGateWithoutReopening(bool restartAndRefresh)
    {
        using var f = new SupersededAcceptanceFailureUnparkFixture();
        await f.RecordRed();
        var cache = new GoalProjectionCache();
        var driver = f.Driver();
        Assert.Equal(GoalLifecycleState.AcceptanceFailed, cache.ResolveState(f.Goal, driver));
        Assert.Equal(1, cache.Count);
        await f.ParkAdvanceUnpark(advance: true);
        if (restartAndRefresh)
        {
            await f.Restart();
            driver = f.Driver();
            await f.RefreshMetadata();
            cache.Invalidate(f.Goal.Id);
            Assert.Equal(0, cache.Count);
            Assert.Equal(GoalLifecycleState.Verified, cache.ResolveState(f.Goal, driver));
            Assert.Equal(1, cache.Count);
        }
        var tasks = f.Tasks();
        var retries = f.Goal.AutomaticAcceptanceRetryCount;
        var result = f.Tick(driver);
        var executed = Assert.IsType<ConductorAdvanceOutcome.Executed>(result.Outcome);
        Assert.Equal("FreshGate", executed.Decision!.Action);
        Assert.Equal(GoalStatus.Verified, f.Goal.Status);
        Assert.Null(f.Goal.LatestAcceptanceFailure);
        Assert.Equal(tasks, f.Tasks());
        Assert.Equal(retries, f.Goal.AutomaticAcceptanceRetryCount);
        Assert.Empty(f.Starts);
        Assert.Empty(f.Gates); // Admission does not fabricate an executed gate.
        f.AssertHistory();

        // Admission must survive reload as well as the pre-consumption restart above.
        await f.Restart();
        driver = f.Driver();
        f.AssertHistory();
        f.Tick(driver);
        f.Tick(driver);
        var gate = Assert.Single(f.Gates);
        Assert.Equal(f.Current.CandidateSha, gate.BranchRevision);
        Assert.Equal(f.Current.MainSha, gate.MainRevision);
        Assert.Equal(tasks, f.Tasks());
        Assert.Equal(retries, f.Goal.AutomaticAcceptanceRetryCount);
        Assert.Empty(f.Starts);
        Assert.DoesNotContain(f.Goal.Timeline, evt => evt.Kind == ProgressKind.TaskRetried);
        f.AssertHistory();
        await f.Restart();
        f.AssertHistory();
        Assert.Null(f.Goal.LatestAcceptanceFailure);
    }
}
