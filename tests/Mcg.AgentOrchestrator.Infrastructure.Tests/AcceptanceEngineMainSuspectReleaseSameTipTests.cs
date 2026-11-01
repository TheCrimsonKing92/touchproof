using Mcg.AgentOrchestrator.App.Orchestration;
using static AcceptanceEngineMainSuspectReleaseTests;

// Parallel-safe: the shared fixture owns a GUID-scoped SQLite store and emergency identity.
public sealed class AcceptanceEngineMainSuspectReleaseSameTipTests
{
    [Fact]
    public async Task ThreePasses_ReleaseWithAllIdentitiesReceiptsAndProgress()
    {
        using var fixture = new Fixture();
        await fixture.FailAsync();
        fixture.Tip.Value = S;
        fixture.Probe.Handler = async (_, _, _) =>
        {
            await AssertTripped(fixture);
            return new(MainSuspectReleaseProbeOutcome.Passed, $"receipt-{fixture.Probe.Calls.Count}");
        };

        Assert.Equal(MainSuspectReleaseCheckOutcome.Released, await fixture.Release.CheckAsync());

        Assert.Equal(3, fixture.Probe.Calls.Count);
        Assert.All(fixture.Probe.Calls, call =>
        {
            Assert.Equal(S, call.Tip);
            Assert.Equal(Tests, call.Tests);
        });
        var cleared = Assert.Single((await fixture.Events.ReadAllAsync())
            .Where(item => item.Kind == PostLandingCanaryEventKind.Cleared));
        var note = cleared.Payload.OperatorNote!;
        Assert.Contains($"suspect={S}", note);
        Assert.Contains($"tip={S}", note);
        Assert.Contains($"tests={string.Join(",", Tests)}", note);
        Assert.Contains("probes=receipt-1,receipt-2,receipt-3", note);
        Assert.Equal(AcceptanceEngineHealth.Healthy, (await fixture.Circuit.ReadAsync()).Health);
        for (var run = 1; run <= 3; run++)
        {
            Assert.Contains($"main-suspect same-tip probe {run}/3 passed", fixture.Lines[run - 1]);
            Assert.Contains($"receipt-{run}", fixture.Lines[run - 1]);
        }
        Assert.Equal($"CANARY_GATE sha={S} result=released reason=same-tip suspect={S}", fixture.Lines[^1]);
        Assert.Equal(4, fixture.Lines.Count);
        Assert.Equal(MainSuspectReleaseCheckOutcome.NotEligible, await fixture.Release.CheckAsync());
        Assert.Equal(3, fixture.Probe.Calls.Count);
        Assert.Equal(4, fixture.Lines.Count);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task FailedRun_KeepsTripAndSuppressesFurtherProbesAtTip(int failedRun)
    {
        using var fixture = new Fixture();
        await fixture.FailAsync();
        fixture.Tip.Value = S;
        fixture.Probe.Handler = (_, _, _) => Task.FromResult(new MainSuspectReleaseProbeResult(
            fixture.Probe.Calls.Count == failedRun
                ? MainSuspectReleaseProbeOutcome.Failed : MainSuspectReleaseProbeOutcome.Passed,
            $"receipt-{fixture.Probe.Calls.Count}"));

        Assert.Equal(MainSuspectReleaseCheckOutcome.StillFailing, await fixture.Release.CheckAsync());
        await AssertTripped(fixture);
        Assert.Equal(failedRun, fixture.Probe.Calls.Count);
        Assert.Contains($"main-suspect same-tip probe {failedRun}/3 failed", fixture.Lines[failedRun - 1]);
        for (var run = 1; run <= failedRun; run++)
            Assert.Contains($"receipt-{run}", fixture.Lines[failedRun - 1]);

        fixture.Probe.Handler = (_, _, _) => Task.FromResult(
            new MainSuspectReleaseProbeResult(MainSuspectReleaseProbeOutcome.Passed, "later-receipt"));
        for (var check = 0; check < 2; check++)
            Assert.Equal(MainSuspectReleaseCheckOutcome.AlreadyResolvedForTip, await fixture.Release.CheckAsync());
        Assert.Equal(failedRun, fixture.Probe.Calls.Count);
        Assert.Equal(failedRun + 1, fixture.Lines.Count);
        await AssertTripped(fixture);

        fixture.Tip.Value = U;
        Assert.Equal(MainSuspectReleaseCheckOutcome.Released, await fixture.Release.CheckAsync());
        Assert.Equal(failedRun + 1, fixture.Probe.Calls.Count);
        Assert.Equal(U, fixture.Probe.Calls[^1].Tip);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnavailableProbe_KeepsTripReportsOutcomeAndHonorsBudget(bool throws)
    {
        using var fixture = new Fixture(MainSuspectReleaseProbeOutcome.CouldNotRun);
        await fixture.FailAsync();
        fixture.Tip.Value = S;
        fixture.Probe.Handler = (_, _, _) => throws
            ? throw new InvalidOperationException("controlled unavailable probe")
            : Task.FromResult(new MainSuspectReleaseProbeResult(
                MainSuspectReleaseProbeOutcome.CouldNotRun, "unavailable-receipt", "runner-unavailable"));

        for (var attempt = 1; attempt <= AcceptanceEngineMainSuspectRelease.MaxReleaseProbeAttemptsPerTip; attempt++)
        {
            Assert.Equal(MainSuspectReleaseCheckOutcome.Deferred, await fixture.Release.CheckAsync());
            Assert.Equal(attempt, fixture.Probe.Calls.Count);
            Assert.Contains("main-suspect same-tip probe 1/3 could-not-run", fixture.Lines[^2]);
            Assert.Contains($"suspect={S}", fixture.Lines[^2]);
            Assert.Contains($"sha={S}", fixture.Lines[^2]);
            Assert.Contains($"tests={string.Join(",", Tests)}", fixture.Lines[^2]);
            if (!throws) Assert.Contains("unavailable-receipt", fixture.Lines[^2]);
            Assert.Contains($"result=release-probe-deferred suspect={S} attempt={attempt}", fixture.Lines[^1]);
            Assert.Contains(throws ? "reason=InvalidOperationException" : "reason=runner-unavailable", fixture.Lines[^1]);
            await AssertTripped(fixture);
        }
        Assert.Equal(MainSuspectReleaseCheckOutcome.AttemptsExhausted, await fixture.Release.CheckAsync());
        Assert.Equal(3, fixture.Probe.Calls.Count);
        Assert.Equal(6, fixture.Lines.Count);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task TipMovesDuringRun_StopsSequenceAndUsesMovedTipPath(int movingRun)
    {
        using var fixture = new Fixture();
        await fixture.FailAsync();
        fixture.Tip.Value = S;
        fixture.Probe.Handler = (_, _, _) =>
        {
            if (fixture.Probe.Calls.Count == movingRun) fixture.Tip.Value = U;
            return Task.FromResult(new MainSuspectReleaseProbeResult(
                MainSuspectReleaseProbeOutcome.Passed, $"receipt-{fixture.Probe.Calls.Count}"));
        };

        Assert.Equal(MainSuspectReleaseCheckOutcome.TipMovedDuringProbe, await fixture.Release.CheckAsync());
        Assert.Equal(movingRun, fixture.Probe.Calls.Count);
        Assert.All(fixture.Probe.Calls, call => Assert.Equal(S, call.Tip));
        await AssertTripped(fixture);

        Assert.Equal(MainSuspectReleaseCheckOutcome.Released, await fixture.Release.CheckAsync());
        Assert.Equal(movingRun + 1, fixture.Probe.Calls.Count);
        Assert.Equal(U, fixture.Probe.Calls[^1].Tip);
        Assert.Contains("result=released reason=main-moved", fixture.Lines[^1]);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task FailureReceiptsChangeDuringRun_StopsWithoutAutomaticClear(int changingRun)
    {
        using var fixture = new Fixture();
        await fixture.FailAsync();
        fixture.Tip.Value = S;
        fixture.Probe.Handler = async (_, _, _) =>
        {
            if (fixture.Probe.Calls.Count == changingRun) await fixture.FailAsync(sha: U);
            return new(MainSuspectReleaseProbeOutcome.Passed, "receipt");
        };

        Assert.Equal(MainSuspectReleaseCheckOutcome.ReceiptsChangedDuringProbe, await fixture.Release.CheckAsync());
        Assert.Equal(changingRun, fixture.Probe.Calls.Count);
        await AssertTripped(fixture);
        Assert.Equal(MainSuspectReleaseCheckOutcome.AlreadyResolvedForTip, await fixture.Release.CheckAsync());
        Assert.Equal(changingRun, fixture.Probe.Calls.Count);
    }

    [Fact]
    public async Task UnavailableThenPassing_ConsumesSharedBudgetWithoutPartialRelease()
    {
        using var fixture = new Fixture(MainSuspectReleaseProbeOutcome.CouldNotRun);
        await fixture.FailAsync();
        fixture.Tip.Value = S;
        Assert.Equal(MainSuspectReleaseCheckOutcome.Deferred, await fixture.Release.CheckAsync());
        fixture.Probe.Handler = (_, _, _) => Task.FromResult(
            new MainSuspectReleaseProbeResult(MainSuspectReleaseProbeOutcome.Passed, "passing-receipt"));

        Assert.Equal(MainSuspectReleaseCheckOutcome.AttemptsExhausted, await fixture.Release.CheckAsync());
        Assert.Equal(3, fixture.Probe.Calls.Count);
        Assert.Contains("main-suspect same-tip probe 1/3 passed", fixture.Lines[^2]);
        Assert.Contains("main-suspect same-tip probe 2/3 passed", fixture.Lines[^1]);
        await AssertTripped(fixture);
        Assert.Equal(MainSuspectReleaseCheckOutcome.AttemptsExhausted, await fixture.Release.CheckAsync());
        Assert.Equal(3, fixture.Probe.Calls.Count);
    }

    [Fact]
    public async Task SecondProbeHeld_SerializesWholeSequenceUntilThreePasses()
    {
        using var fixture = new Fixture();
        await fixture.FailAsync();
        fixture.Tip.Value = S;
        var secondEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishSecond = new TaskCompletionSource<MainSuspectReleaseProbeResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Probe.Handler = (_, _, _) =>
        {
            if (fixture.Probe.Calls.Count != 2)
                return Task.FromResult(new MainSuspectReleaseProbeResult(MainSuspectReleaseProbeOutcome.Passed, "receipt"));
            secondEntered.SetResult();
            return finishSecond.Task;
        };
        var check = fixture.Release.CheckAsync();
        try
        {
            // Hang detector only: the assertion concerns the held probe, never elapsed time.
            await secondEntered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(MainSuspectReleaseCheckOutcome.InFlight, await fixture.Release.CheckAsync());
            Assert.Equal(2, fixture.Probe.Calls.Count);
            await AssertTripped(fixture);
        }
        finally
        {
            finishSecond.TrySetResult(new(MainSuspectReleaseProbeOutcome.Passed, "second-receipt"));
            await check;
        }
        Assert.Equal(MainSuspectReleaseCheckOutcome.Released, await check);
        Assert.Equal(3, fixture.Probe.Calls.Count);
    }

    private static async Task AssertTripped(Fixture fixture)
    {
        Assert.Equal(AcceptanceEngineHealth.Unhealthy, (await fixture.Circuit.ReadAsync()).Health);
        Assert.Empty((await fixture.Events.ReadAllAsync()).Where(item => item.Kind == PostLandingCanaryEventKind.Cleared));
    }
}
