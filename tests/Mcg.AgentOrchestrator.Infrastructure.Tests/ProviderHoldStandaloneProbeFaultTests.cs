using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: private workspace, fake provider, injected clock and diagnostic sink.
public sealed class ProviderHoldStandaloneProbeFaultTests
{
    [Theory]
    [InlineData("available")]
    [InlineData("exhausted")]
    public void WriterFailure_PreservesRecordedResultAndChangedGoal(string outcome)
    {
        using var fixture = new ProviderHoldStandaloneProbeTests.Fixture(outcome);
        var diagnostics = new List<string>();
        var writer = new ConductEventLogWriter(Path.Combine(fixture.Workspace.LogDirectory, "failing.log"),
            beforeAppendCommit: () => throw new IOException("fixture write failure"));
        var probe = new FakeProbe(outcome);
        var prober = new ProviderHoldStandaloneProbe(probe, writer, fixture.Preflight,
            diagnostic: diagnostics.Add);

        fixture.Clock.UtcNow = fixture.Due;
        var changed = prober.ObserveTick(fixture.Kernel, fixture.Due);
        prober.ObserveTick(fixture.Kernel, fixture.Due);

        Assert.Equal(fixture.Source.Id, Assert.Single(changed));
        Assert.Equal(1, probe.Calls);
        var note = Assert.Single(fixture.Source.Timeline.Where(evt =>
            ProviderBudgetHoldSchedules.TryParseStandaloneResult(evt, out _)));
        Assert.True(ProviderBudgetHoldSchedules.TryParseStandaloneResult(note, out var result));
        Assert.Equal(outcome, result.Outcome);
        var rows = ProviderUsageHoldView.Read(fixture.Kernel.ProviderHoldScope, fixture.Due);
        if (outcome == "available") Assert.Empty(rows);
        else Assert.Equal(fixture.Due.AddMinutes(60), Assert.Single(rows).Schedule!.NextProbeAt);
        Assert.Contains("outcome=failed", Assert.Single(diagnostics));
        Assert.Contains("exception=IOException", diagnostics[0]);
        Assert.Contains("message=fixture%20write%20failure", diagnostics[0]);
    }

    [Fact]
    public void PreflightFailure_ReportsFaultAndLeavesDueTimeUnclaimed()
    {
        using var fixture = new ProviderHoldStandaloneProbeTests.Fixture("available");
        fixture.NewGoal(); // Ensure the assigned-task preflight is actually reached.
        var fail = true;
        var probe = new FakeProbe("available");
        var diagnostics = new List<string>();
        var prober = new ProviderHoldStandaloneProbe(probe, fixture.Writer,
            (goal, task, scope) => fail ? throw new IOException("fixture preflight fault") : fixture.Preflight(goal, task, scope),
            diagnostic: diagnostics.Add);

        fixture.Clock.UtcNow = fixture.Due;
        Assert.Empty(prober.ObserveTick(fixture.Kernel, fixture.Due));
        Assert.Equal(0, probe.Calls);
        Assert.Single(ProviderUsageHoldView.Read(fixture.Kernel.ProviderHoldScope, fixture.Due));
        var check = Assert.Single(fixture.Events());
        Assert.Equal("provider-hold-check", check.EventKind);
        Assert.Contains("outcome=failed reason=IOException%3Afixture%20preflight%20fault", check.Detail);
        Assert.Single(diagnostics);

        fail = false;
        Assert.Equal(fixture.Source.Id, Assert.Single(prober.ObserveTick(fixture.Kernel, fixture.Due)));
        Assert.Equal(1, probe.Calls);
        Assert.Empty(ProviderUsageHoldView.Read(fixture.Kernel.ProviderHoldScope, fixture.Due));
    }

    [Fact]
    public void PostProbeFault_IsReportedWithoutClearingHoldOrRepeatingCall()
    {
        using var fixture = new ProviderHoldStandaloneProbeTests.Fixture("available");
        var probe = new FakeProbe("available");
        var diagnostics = new List<string>();
        var prober = new ProviderHoldStandaloneProbe(probe, fixture.Writer, fixture.Preflight,
            utcNow: () => throw new IOException("fixture completion fault"), diagnostic: diagnostics.Add);

        fixture.Clock.UtcNow = fixture.Due;
        Assert.Empty(prober.ObserveTick(fixture.Kernel, fixture.Due));
        Assert.Empty(prober.ObserveTick(fixture.Kernel, fixture.Due));

        Assert.Equal(1, probe.Calls);
        Assert.Single(ProviderUsageHoldView.Read(fixture.Kernel.ProviderHoldScope, fixture.Due));
        Assert.DoesNotContain(fixture.Source.Timeline, evt => ProviderBudgetHoldSchedules.TryParseStandaloneResult(evt, out _));
        Assert.Contains("outcome=failed reason=IOException%3Afixture%20completion%20fault", Assert.Single(fixture.Events()).Detail);
        Assert.Single(diagnostics);
    }

    [Fact]
    public void RowFailure_ContinuesProbingOtherBindings()
    {
        using var fixture = new ProviderHoldStandaloneProbeTests.Fixture("available");
        var other = fixture.NewGoal();
        var task = other.Tasks.Single();
        var started = fixture.Clock.UtcNow.AddSeconds(-1);
        fixture.Kernel.RecordTaskDispatch(other.Id, task.Id, new("claude-cli", "fixture", fixture.Workspace.ExecutionDirectory,
            started, ProviderName: "Anthropic"));
        fixture.Kernel.RecordTaskVerification(other.Id, task.Id, new("fixture", fixture.Workspace.ExecutionDirectory,
            1, "", "fixture usage limit", fixture.Clock.UtcNow,
            ProviderFailureKind: ProviderFailureKind.BudgetExhausted, DispatchStartedAt: started));
        fixture.Kernel.ReportTaskProgress(other.Id, task.Id, WorkTaskStatus.Failed, "Fixture provider failure");
        fixture.Kernel.EscalateTaskFailure(other.Id, task.Id, "Fixture provider failure");
        fixture.NewGoal();
        Assert.Equal(2, ProviderUsageHoldView.Read(fixture.Kernel.ProviderHoldScope, fixture.Due).Count);
        var preflights = 0;
        var probe = new FakeProbe("available");
        var diagnostics = new List<string>();
        var prober = new ProviderHoldStandaloneProbe(probe, fixture.Writer,
            (goal, assigned, scope) => ++preflights == 1
                ? throw new IOException("fixture first row fault") : fixture.Preflight(goal, assigned, scope),
            diagnostic: diagnostics.Add);

        fixture.Clock.UtcNow = fixture.Due;
        Assert.Single(prober.ObserveTick(fixture.Kernel, fixture.Due));

        Assert.Equal(1, probe.Calls);
        Assert.Single(ProviderUsageHoldView.Read(fixture.Kernel.ProviderHoldScope, fixture.Due));
        Assert.Contains(fixture.Events(), evt => evt.Detail.Contains("outcome=failed", StringComparison.Ordinal));
        Assert.Contains(fixture.Events(), evt => evt.Detail.Contains("outcome=available", StringComparison.Ordinal));
        Assert.Single(diagnostics);
    }

    [Fact]
    public void BrokenFallbackSink_DoesNotLoseChangedGoal()
    {
        using var fixture = new ProviderHoldStandaloneProbeTests.Fixture("available");
        var writes = 0;
        var diagnostics = 0;
        var writer = new ConductEventLogWriter(Path.Combine(fixture.Workspace.LogDirectory, "failing.log"),
            beforeAppendCommit: () => { writes++; throw new IOException("fixture sink fault"); });
        var probe = new FakeProbe("available");
        var prober = new ProviderHoldStandaloneProbe(probe, writer, fixture.Preflight,
            diagnostic: _ => { diagnostics++; throw new IOException("fixture stderr fault"); });

        fixture.Clock.UtcNow = fixture.Due;
        Assert.Equal(fixture.Source.Id, Assert.Single(prober.ObserveTick(fixture.Kernel, fixture.Due)));

        Assert.Equal(1, writes);
        Assert.Equal(1, diagnostics);
        Assert.Equal(1, probe.Calls);
        Assert.Empty(ProviderUsageHoldView.Read(fixture.Kernel.ProviderHoldScope, fixture.Due));
    }

    [Fact]
    public void ResetDuringProbe_ReportsDiscardedResult()
    {
        using var fixture = new ProviderHoldStandaloneProbeTests.Fixture("available");
        var reset = fixture.Due.AddDays(1);
        fixture.DuringProbe = () => fixture.Kernel.RecordTaskNote(fixture.Source.Id, fixture.Source.Tasks.Single().Id,
            ProviderBudgetHoldSchedules.FormatReset(new("xai::<provider-default>", reset, "fixture-reset")));
        fixture.Clock.UtcNow = fixture.Due;

        fixture.Tick();

        Assert.Equal(1, fixture.Calls);
        Assert.Equal(reset, Assert.Single(ProviderUsageHoldView.Read(fixture.Kernel.ProviderHoldScope, fixture.Due)).Schedule!.NextProbeAt);
        Assert.DoesNotContain(fixture.Source.Timeline, evt => ProviderBudgetHoldSchedules.TryParseStandaloneResult(evt, out _));
        var check = Assert.Single(fixture.Events().Where(evt => evt.EventKind == "provider-hold-check"));
        Assert.Contains("outcome=failed reason=stale-probe-result", check.Detail);
    }

    private sealed class FakeProbe(string outcome) : IProviderHoldAvailabilityProbe
    {
        internal int Calls;
        public Task<ProviderHoldProbeResult> CheckAsync(ProviderBudgetExhaustionHold hold, TaskDispatchRecord dispatch,
            CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new ProviderHoldProbeResult(outcome, "fixture-result"));
        }
    }
}
