using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: stores, landing identities and clock are fixture-local; no processes run.
public sealed class SelectedExecutionBackstopYieldTests
{
    private static TimeSpan AgeBound => TimeSpan.FromHours(SelectedExecutionBackstopYieldPolicy.MaximumOldestLandingAgeHours);

    [Theory]
    [InlineData(GoalPriority.Low)]
    [InlineData(GoalPriority.Normal)]
    [InlineData(GoalPriority.Critical)]
    public async Task LaterVerifiedGoal_YieldsUntilWaitingListIsEmpty(GoalPriority priority)
    {
        using var fixture = new SelectedExecutionBackstopFixture();
        await fixture.Land();
        await fixture.Service(suppressed: true);
        var queued = (await fixture.Store.ReadAsync())!.Request!;
        var waiting = GoalAt(queued.QueuedAt.AddMinutes(1), priority);
        await fixture.Coordinator.ServiceAsync(() => fixture.Head, [waiting], true, false);
        await fixture.Coordinator.DrainAsync();
        Assert.Empty(fixture.Calls);
        Assert.Equal(0, (await fixture.Store.ReadAsync())!.Request!.Attempts);
        await fixture.Finish();
        Assert.Equal(queued.Id, Assert.Single(fixture.Calls).Id);
        AssertDecision(fixture, "started", "idle", 1);
    }

    [Fact]
    public async Task LandingCount_JustBelowYields_AtBoundStarts()
    {
        using var fixture = new SelectedExecutionBackstopFixture();
        var waiting = GoalAt(fixture.Now.AddMinutes(-1));
        for (var ordinal = 1; ordinal < SelectedExecutionBackstopYieldPolicy.MaximumUncoveredLandings; ordinal++)
            await fixture.Land("same-goal", "main-" + ordinal);
        await fixture.Coordinator.ServiceAsync(() => fixture.Head, [waiting], true, false);
        await fixture.Coordinator.DrainAsync();
        Assert.Empty(fixture.Calls);
        // A readable full-gate landing does not contribute to the uncovered count.
        await fixture.Land("full-gate", "full-gate-commit", AcceptanceSelectedExecutionRecord.Disabled);
        await fixture.Coordinator.ServiceAsync(() => fixture.Head, [waiting], true, false);
        await fixture.Coordinator.DrainAsync();
        Assert.Empty(fixture.Calls);
        fixture.Head = "unreadable-commit";
        await fixture.Coordinator.NoteLandingAsync("unreadable", fixture.Head, null, "main-0");
        await fixture.Coordinator.ServiceAsync(() => fixture.Head, [waiting], true, false);
        await fixture.Coordinator.DrainAsync();
        Assert.Equal(fixture.Head, Assert.Single(fixture.Calls).ToCommit);
        AssertDecision(fixture, "started", "bound-landings", SelectedExecutionBackstopYieldPolicy.MaximumUncoveredLandings);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(1L)]
    public async Task OldestAge_JustBelowYields_AtOrAboveBoundStarts(long extraTicks)
    {
        using var fixture = new SelectedExecutionBackstopFixture();
        var landedAt = fixture.Now;
        await fixture.Land();
        var waiting = GoalAt(landedAt.AddMinutes(-1));
        fixture.Now = landedAt + AgeBound - TimeSpan.FromTicks(1);
        await fixture.Coordinator.ServiceAsync(() => fixture.Head, [waiting], true, false);
        await fixture.Coordinator.DrainAsync();
        Assert.Empty(fixture.Calls);
        fixture.Now = landedAt + AgeBound + TimeSpan.FromTicks(extraTicks);
        await fixture.Coordinator.ServiceAsync(() => fixture.Head, [waiting], true, false);
        await fixture.Coordinator.DrainAsync();
        Assert.Single(fixture.Calls);
        AssertDecision(fixture, "started", "bound-age", 1);
    }

    [Fact]
    public async Task InfrastructureRetry_AgeUsesLandingDespiteResetQueueTime()
    {
        using var fixture = new SelectedExecutionBackstopFixture();
        var landedAt = fixture.Now;
        await fixture.Land();
        fixture.Run = _ =>
        {
            fixture.Now = landedAt + AgeBound - TimeSpan.FromMinutes(30);
            return Task.FromResult(SelectedExecutionBackstopOutcome.Infrastructure("fixture-apparatus-fault"));
        };
        await fixture.Finish();
        var retry = (await fixture.Store.ReadAsync())!.Request!;
        Assert.Equal(fixture.Now, retry.QueuedAt);
        Assert.NotEqual(landedAt, retry.QueuedAt);
        Assert.True(retry.RetryPending);
        Assert.Single(fixture.Calls);
        fixture.Run = _ => Task.FromResult(SelectedExecutionBackstopOutcome.Green);
        var resumed = fixture.CreateCoordinator();
        var waiting = GoalAt(landedAt);
        fixture.Now = landedAt + AgeBound - TimeSpan.FromTicks(1);
        await resumed.ServiceAsync(() => fixture.Head, [waiting], true, false);
        await resumed.DrainAsync();
        Assert.Single(fixture.Calls);
        fixture.Now = landedAt + AgeBound;
        await resumed.ServiceAsync(() => fixture.Head, [waiting], true, false);
        await resumed.DrainAsync();
        Assert.Equal(2, fixture.Calls.Count);
        Assert.Equal(retry.Id, fixture.Calls.Last().Id);
        Assert.Equal(2, fixture.Calls.Last().Attempts);
        AssertDecision(fixture, "resumed", "bound-age", 1);
    }

    [Fact]
    public async Task AccumulatedLandings_OneRequestCoversAllAndReportsCount()
    {
        using var fixture = new SelectedExecutionBackstopFixture();
        var waiting = GoalAt(fixture.Now.AddMinutes(1));
        SelectedExecutionBackstopRequest? queued = null;
        for (var ordinal = 1; ordinal <= 3; ordinal++)
        {
            fixture.Head = "main-" + ordinal;
            await fixture.Land("same-goal", fixture.Head);
            await fixture.Coordinator.ServiceAsync(() => fixture.Head, [waiting], true, false);
            await fixture.Coordinator.DrainAsync();
            Assert.Empty(fixture.Calls);
            queued ??= (await fixture.Store.ReadAsync())!.Request;
            fixture.Now = fixture.Now.AddMinutes(1);
        }
        await fixture.Finish();
        var call = Assert.Single(fixture.Calls);
        Assert.Equal(queued!.Id, call.Id);
        Assert.Equal("main-0", call.FromCommit);
        Assert.Equal("main-3", call.ToCommit);
        Assert.Equal(3, call.ThroughOrdinal);
        Assert.Equal("same-goal", Assert.Single(call.GoalIds));
        AssertDecision(fixture, "started", "idle", 3);
        var state = (await fixture.Store.ReadAsync())!;
        Assert.Equal("main-3", state.CoveredCommit);
        Assert.Empty(state.Uncovered);
        Assert.Empty(state.RecordedLandingInstants);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReachedBound_CapacityAndSuppressionStillHold(bool useAge)
    {
        using var fixture = new SelectedExecutionBackstopFixture();
        var waiting = GoalAt(fixture.Now.AddMinutes(-1));
        var count = useAge ? 1 : SelectedExecutionBackstopYieldPolicy.MaximumUncoveredLandings;
        for (var ordinal = 1; ordinal <= count; ordinal++)
            await fixture.Land("goal-" + ordinal, "main-" + ordinal);
        // Both bounds hold in the count case, exercising deterministic rule precedence.
        fixture.Now += AgeBound;
        await fixture.Coordinator.ServiceAsync(() => fixture.Head, [waiting], false, false);
        await fixture.Coordinator.ServiceAsync(() => fixture.Head, [waiting], true, true);
        await fixture.Coordinator.DrainAsync();
        Assert.Empty(fixture.Calls);
        Assert.Equal(0, (await fixture.Store.ReadAsync())!.Request!.Attempts);
        await fixture.Coordinator.ServiceAsync(() => fixture.Head, [waiting], true, false);
        await fixture.Coordinator.DrainAsync();
        Assert.Single(fixture.Calls);
        AssertDecision(fixture, "started", useAge ? "bound-age" : "bound-landings", count);
    }

    [Fact]
    public async Task LegacyLanding_FirstObservationSurvivesRestart()
    {
        using var fixture = new SelectedExecutionBackstopFixture();
        const string payload = """
            {"RangeStartCommit":"main-0","LastLandingOrdinal":1,
             "Landings":[{"Ordinal":1,"GoalId":"legacy","Commit":"main-1","Applied":true,"RecordUnreadable":false}]}
            """;
        await fixture.Events.AppendAsync(new(SelectedExecutionBackstopStore.EventType, null, "legacy", "legacy", null,
            payload, fixture.Now - AgeBound));
        var observedAt = fixture.Now;
        var waiting = GoalAt(observedAt.AddMinutes(-1));
        await fixture.Coordinator.ServiceAsync(() => fixture.Head, [waiting], true, false);
        await fixture.Coordinator.DrainAsync();
        Assert.Empty(fixture.Calls);
        Assert.Equal(observedAt, (await fixture.Store.ReadAsync())!.OldestUncoveredAt);
        var store = new SelectedExecutionBackstopStore(new SqliteRunEventStore(Path.Combine(fixture.Home, "run-events.db")));
        var resumed = new SelectedExecutionBackstopCoordinator(store, fixture, fixture.Items, fixture.Home,
            () => fixture.Now, fixture.Lines.Enqueue);
        fixture.Now = observedAt + AgeBound - TimeSpan.FromTicks(1);
        await resumed.ServiceAsync(() => fixture.Head, [waiting], true, false);
        await resumed.DrainAsync();
        Assert.Empty(fixture.Calls);
        Assert.Equal(observedAt, (await store.ReadAsync())!.OldestUncoveredAt);
        fixture.Now = observedAt + AgeBound;
        await resumed.ServiceAsync(() => fixture.Head, [waiting], true, false);
        await resumed.DrainAsync();
        Assert.Single(fixture.Calls);
        AssertDecision(fixture, "started", "bound-age", 1);
    }

    [Fact]
    public async Task InterruptedRequest_YieldsThenResumesItsFixedRangeAtAgeBound()
    {
        using var fixture = new SelectedExecutionBackstopFixture();
        var oldestAt = fixture.Now;
        fixture.Head = "main-1";
        await fixture.Land();
        await fixture.Service(suppressed: true);
        var state = (await fixture.Store.ReadAsync())!;
        await fixture.Store.WriteAsync("interrupted", state with { Request = state.Request! with { Attempts = 1 } }, fixture.Now);
        fixture.Now += TimeSpan.FromMinutes(30);
        var newerAt = fixture.Now;
        fixture.Head = "main-2";
        await fixture.Land("goal-2", fixture.Head);
        var resumed = fixture.CreateCoordinator();
        var waiting = GoalAt(oldestAt.AddMinutes(1));
        await resumed.ServiceAsync(() => fixture.Head, [waiting], true, false);
        await resumed.DrainAsync();
        Assert.Empty(fixture.Calls);
        fixture.Now = oldestAt + AgeBound;
        await resumed.ServiceAsync(() => fixture.Head, [waiting], true, false);
        await resumed.DrainAsync();
        var call = Assert.Single(fixture.Calls);
        Assert.Equal("main-1", call.ToCommit);
        Assert.Equal(1, call.ThroughOrdinal);
        AssertDecision(fixture, "resumed", "bound-age", 1);
        state = (await fixture.Store.ReadAsync())!;
        Assert.Equal("goal-2", Assert.Single(state.Uncovered).GoalId);
        Assert.Equal(newerAt, state.OldestUncoveredAt);
        await resumed.ServiceAsync(() => fixture.Head, [waiting], true, false);
        await resumed.DrainAsync();
        Assert.Single(fixture.Calls);
    }

    private static void AssertDecision(SelectedExecutionBackstopFixture fixture, string result, string rule, int covered)
    {
        var line = Assert.Single(fixture.Lines.Where(line => line.Contains("result=" + result + " ", StringComparison.Ordinal)));
        Assert.Contains("rule=" + rule, line.Split(' '));
        Assert.Contains("covered=" + covered, line.Split(' '));
    }

    private static Goal GoalAt(DateTimeOffset verifiedAt, GoalPriority priority = GoalPriority.Normal)
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("backstop yield fixture");
        var snapshot = kernel.ExportSnapshot();
        kernel = AgentOrchestratorKernel.FromSnapshot(snapshot with { Goals = snapshot.Goals.Select(item => item with
            { Timeline = item.Timeline.Select(evt => evt with { Kind = ProgressKind.GoalPolicyDecision,
                Message = "Verified", OccurredAt = verifiedAt }).ToArray() }).ToArray() });
        kernel.SetGoalPriority(goal.Id, priority, "test", "fixture priority");
        return kernel.GetGoal(goal.Id);
    }
}
