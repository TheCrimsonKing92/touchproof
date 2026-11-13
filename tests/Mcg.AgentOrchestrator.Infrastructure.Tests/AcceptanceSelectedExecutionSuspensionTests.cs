using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using static AcceptanceSelectedExecutionFixture;

// Parallel-safe: candidate selection is injected; marker and attention stores are fixture-local.
public sealed class AcceptanceSelectedExecutionSuspensionTests
{
    [Fact]
    public async Task UnchangedOpenMissMarkerIsNotReplacedWhileAGateReadsIt()
    {
        using var fixture = new SelectedExecutionBackstopFixture();
        fixture.Run = _ => Task.FromResult(new SelectedExecutionBackstopOutcome("red", ["Candidate.X.Missed"], ["lane"]));
        await fixture.Land();
        await fixture.Finish();
        var miss = (await fixture.Store.ReadAsync())!.OpenMiss!;
        var path = AcceptanceSelectedExecutionSessionSuspension.MarkerPath(fixture.Home);
        // Windows denies replacement while this reader is open. The service must leave
        // identical content untouched, rather than retrying an unnecessary marker write.
        using (var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            await fixture.Service(capacity: false);
            Assert.Equal(miss.Id, (await AcceptanceSelectedExecutionSessionSuspension.ReadAsync("unused", fixture.Home))!.MissId);
        }
        AcceptanceSelectedExecutionSessionSuspension.Write(fixture.Home, new("replacement"));
        Assert.Equal("replacement", (await AcceptanceSelectedExecutionSessionSuspension.ReadAsync("unused", fixture.Home))!.MissId);
    }

    [Fact]
    public async Task OpenMissSuspendsEnforceAndResolutionRestoresSameSelectionImmediately()
    {
        using var fixture = new SelectedExecutionBackstopFixture();
        fixture.Run = _ => Task.FromResult(new SelectedExecutionBackstopOutcome("red", ["Candidate.X.Missed"], ["lane"]));
        await fixture.Land();
        await fixture.Finish();
        var miss = (await fixture.Store.ReadAsync())!.OpenMiss!;
        var suspension = await AcceptanceSelectedExecutionSessionSuspension.ReadAsync("unused", fixture.Home);
        var suspended = Create(AcceptanceSelectedExecutionMode.Enforce, suspension);
        var observed = Observe(suspended);
        Assert.False(suspended.Applied);
        Assert.Equal(Lanes.Select(lane => lane.Name), observed.Scheduled);
        Assert.Empty(observed.Skipped);
        var record = Assert.IsType<AcceptanceSelectedExecutionRecord>(observed.SelectedExecution);
        Assert.Equal("Shadow", record.Mode);
        Assert.Equal("suspended-by-backstop-miss", record.SuspensionReason);
        Assert.Equal(miss.Id, record.BackstopMissId);
        Assert.False(record.Applied);
        Assert.False(record.StructuralCoverageScoped);
        Assert.True(await fixture.Items.TryResolveAsync(miss.CorrelationKey, "operator verified fix"));
        // Gate entry reads the resolution directly; no conductor reconciliation is required.
        Assert.Null(await AcceptanceSelectedExecutionSessionSuspension.ReadAsync("unused", fixture.Home));
        var restored = Create(AcceptanceSelectedExecutionMode.Enforce,
            await AcceptanceSelectedExecutionSessionSuspension.ReadAsync("unused", fixture.Home));
        Assert.DoesNotContain("Balanced 2", Observe(restored).Scheduled);
        Assert.True(restored.Applied);
        await fixture.Service(capacity: false);
        Assert.Null((await fixture.Store.ReadAsync())!.OpenMiss);
    }

    [Fact]
    public void NoMissIsNegativeControlAndOffAndShadowRemainUnaffected()
    {
        var normal = Create(AcceptanceSelectedExecutionMode.Enforce, null);
        Assert.DoesNotContain("Balanced 2", Observe(normal).Scheduled);
        Assert.True(normal.Applied);
        foreach (var mode in new[] { AcceptanceSelectedExecutionMode.Off, AcceptanceSelectedExecutionMode.Shadow })
        {
            var ordinary = Observe(Create(mode, null)).SelectedExecution!;
            var withMiss = Observe(Create(mode, new("miss"))).SelectedExecution!;
            Assert.Equal(ordinary.Mode, withMiss.Mode);
            Assert.Equal(ordinary.Decision, withMiss.Decision);
            Assert.Equal(ordinary.Applied, withMiss.Applied);
            Assert.Null(withMiss.SuspensionReason);
            Assert.Null(withMiss.BackstopMissId);
        }
    }

    [Fact]
    public async Task CorruptMarkerFailsClosedAndProjectHomeDefinesCrossProcessIdentity()
    {
        using var fixture = new SelectedExecutionBackstopFixture();
        File.WriteAllText(AcceptanceSelectedExecutionSessionSuspension.MarkerPath(fixture.Home), "{corrupt");
        Assert.Equal(fixture.Home, AcceptanceSelectedExecutionSessionSuspension.Home("unrelated-candidate", fixture.Home));
        var suspension = await AcceptanceSelectedExecutionSessionSuspension.ReadAsync("unrelated-candidate", fixture.Home);
        Assert.NotNull(suspension);
        var session = Create(AcceptanceSelectedExecutionMode.Enforce, suspension);
        Assert.Equal(Lanes.Select(lane => lane.Name), Observe(session).Scheduled);
        Assert.False(session.Applied);
    }

    [Fact]
    public async Task ExternalStateResolutionUsesCanonicalStoreAndCannotClearAnotherOpenMiss()
    {
        using var fixture = new SelectedExecutionBackstopFixture();
        var stateHome = Path.Combine(fixture.Home, "external-state");
        var externalItems = CollaborationItemStore.ForDirectory(stateHome);
        await externalItems.RaiseAsync(CollaborationItemType.Verify, null, "external miss", "record", "selected-execution-backstop-miss:external");
        AcceptanceSelectedExecutionSessionSuspension.Write(fixture.Home, new("external", stateHome), stateHome);
        var external = await AcceptanceSelectedExecutionSessionSuspension.ReadAsync("unrelated-candidate", fixture.Home);
        Assert.Equal("external", external!.MissId);
        Assert.Equal(stateHome, external.StateDirectory);
        Assert.True(await externalItems.TryResolveAsync("selected-execution-backstop-miss:external", "operator fixed external miss"));
        Assert.Null(await AcceptanceSelectedExecutionSessionSuspension.ReadAsync("unrelated-candidate", fixture.Home));
        await fixture.Items.RaiseAsync(CollaborationItemType.Verify, null, "other miss", "record", "selected-execution-backstop-miss:other");
        AcceptanceSelectedExecutionSessionSuspension.Write(fixture.Home, new("other"));
        AcceptanceSelectedExecutionSessionSuspension.Write(fixture.Home, null, stateHome);
        Assert.Equal("other", (await AcceptanceSelectedExecutionSessionSuspension.ReadAsync("unrelated-candidate", fixture.Home))!.MissId);
        Assert.True(await fixture.Items.TryResolveAsync("selected-execution-backstop-miss:other", "operator fixed remaining miss"));
        Assert.Null(await AcceptanceSelectedExecutionSessionSuspension.ReadAsync("unrelated-candidate", fixture.Home));
    }

    private static AcceptanceSelectedExecutionSession Create(AcceptanceSelectedExecutionMode mode,
        AcceptanceSelectedExecutionSessionSuspension? suspension) =>
        new(mode, "in-memory-candidate", Changes, read: (_, _) =>
            new(ReverseDependencyTestSelection.Resolved(["A"]), Classes, ["Candidate.Guard"]), suspension: suspension);
}
