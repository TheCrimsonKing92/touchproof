using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: private workspace/database and injected clocks; no provider calls.
public sealed class ProviderHoldOverduePresentationTests
{
    [Fact]
    public async Task OverdueWithoutAssignedTask_StatusAndConsoleShowUnverifiedBalance()
    {
        using var fixture = new ProviderHoldStandaloneProbeTests.Fixture("available");
        fixture.Clock.UtcNow = fixture.Due.AddDays(9);
        var row = Assert.Single(ProviderUsageHoldView.Read(fixture.Kernel.ProviderHoldScope, fixture.Clock.UtcNow));
        Assert.Equal(fixture.Due, row.OverdueSince);
        await InfrastructureTestSupport.CreateMigratedStateRepository(fixture.Workspace.SqliteStatePath)
            .SaveAsync(fixture.Kernel, TestContext.Current.CancellationToken);
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(0, CliConductorCommand.Run(["conductor", "status"], fixture.Workspace,
            lockProbe: new ConductorVerbStartTests.FixedProbe(null), bootTime: () => fixture.Due.AddDays(-1),
            mainCommit: () => "fixture", output: output, error: error, time: new FixedTime(fixture.Clock.UtcNow)));
        Assert.Equal("", error.ToString());
        Assert.Contains("state=probe-overdue", output.ToString());
        Assert.Contains($"due={ProviderBudgetHoldSchedules.FormatInstant(fixture.Due)}", output.ToString());
        Assert.Contains("balance=unverified since", output.ToString());
        Assert.DoesNotContain("last_probe=exhausted |", output.ToString());
        var model = OwnerFleetViewModelBuilder.Build(RemoteExecutorReport.Build([], [], []), true,
            TimeZoneInfo.Utc, providerHolds: [row]);
        var console = OwnerFleetDiagram.Render(model, 160);
        Assert.Contains("probe-overdue", console);
        Assert.Contains("probe due: 2026-10-17 11:38 +00:00", console);
        Assert.Contains("balance unverified since 2026-10-17 11:08 +00:00", console);
        Assert.DoesNotContain("has no usage balance left", console);
        fixture.Watch.ObserveTick(fixture.Kernel.ProviderHoldScope, fixture.Clock.UtcNow);
        var overdue = Assert.Single(fixture.Events().Where(evt => evt.EventKind == "provider-hold-overdue"));
        Assert.True(OwnerProviderHoldNarration.TryNarrate(overdue, out var phrase, out var reason, out _, out _));
        Assert.Contains("balance unverified", phrase);
        Assert.Contains(ProviderBudgetHoldSchedules.FormatInstant(fixture.Due), reason);
        var activity = OwnerActivityNarrator.Narrate(fixture.Events(), _ => "");
        Assert.Contains(activity, item => item.Phrase.Contains("balance unverified", StringComparison.Ordinal));
        Assert.DoesNotContain(activity, item => item.Phrase.Contains("has no usage balance left", StringComparison.Ordinal));
        fixture.Watch.ObserveTick(fixture.Kernel.ProviderHoldScope, fixture.Clock.UtcNow);
        Assert.Single(fixture.Events().Where(evt => evt.EventKind == "provider-hold-overdue"));
    }

    [Fact]
    public void HistoricalExhaustion_IsLabeledHistoricalAfterScheduledProbeExpires()
    {
        using var fixture = new ProviderHoldStandaloneProbeTests.Fixture("exhausted");
        fixture.Clock.UtcNow = fixture.Due;
        fixture.Tick();
        fixture.Clock.UtcNow = fixture.Due.AddMinutes(60);
        var row = Assert.Single(ProviderUsageHoldView.Read(fixture.Kernel.ProviderHoldScope, fixture.Clock.UtcNow));
        var line = ProviderUsageHoldView.FormatStatusLine(row);
        Assert.Contains("state=probe-overdue", line);
        Assert.Contains("last_probe=exhausted (historical)", line);
        Assert.DoesNotContain("last_probe=exhausted |", line);
        Assert.Equal(fixture.Due, row.LastObservedAt);
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
