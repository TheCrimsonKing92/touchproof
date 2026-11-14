using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: all ordering facts and live-gate observations are supplied by the test.
public sealed class SelectedExecutionBackstopAdmissionTests
{
    [Fact]
    public async Task CapacityAndSuppressionHoldRequestAndReservationCountsExactlyOnce()
    {
        using var fixture = new SelectedExecutionBackstopFixture();
        var release = new TaskCompletionSource<SelectedExecutionBackstopOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Run = _ => release.Task;
        await fixture.Land();
        await fixture.Service(capacity: false);
        await fixture.Service(suppressed: true);
        Assert.Empty(fixture.Calls);
        Assert.NotNull((await fixture.Store.ReadAsync())!.Request);
        await fixture.Service();
        try
        {
            await fixture.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30)); // Hang-only: admitted runner did not enter.
            var id = Assert.IsType<string>(fixture.Coordinator.ActiveRequestId);
            var census = ConductorBatchLoop.BuildLiveAcceptanceCensus([], new HashSet<string>(), new([]), [], id);
            Assert.Equal(1, census.OccupiedCount);
            Assert.False(ConductorBatchLoop.DecideLiveAcceptanceAdmission(census, 1).IsAdmitted);
            var heartbeat = new GateLoadContextProbe.LiveGateOccupant(42, null, 0, TimeSpan.Zero,
                RunClass: GateHeartbeatRunClass.Acceptance, RunId: id);
            var withHeartbeat = ConductorBatchLoop.BuildLiveAcceptanceCensus([], new HashSet<string>(), new([]), [heartbeat], id);
            Assert.Equal(1, fixture.Coordinator.Reserve(withHeartbeat).OccupiedCount);
            Assert.Single(fixture.Calls);
        }
        finally { release.TrySetResult(SelectedExecutionBackstopOutcome.Green); await fixture.Coordinator.DrainAsync(); }
        Assert.Equal(0, fixture.Coordinator.RunningCount);
    }

    [Fact]
    public async Task ThreeOvertakesPersistAcrossRestartButStillYieldToWaitingGoals()
    {
        using var fixture = new SelectedExecutionBackstopFixture();
        await fixture.Land();
        await fixture.Service(suppressed: true);
        var high = Enumerable.Range(1, 3).Select(index => GoalAt(fixture.Now.AddMinutes(index), GoalPriority.High)).ToArray();
        foreach (var goal in high)
        {
            await fixture.Coordinator.ServiceAsync(() => fixture.Head, [goal], true, false);
            Assert.Empty(fixture.Calls);
            await fixture.Coordinator.ObserveServedAsync([goal]);
        }
        Assert.Equal(3, (await fixture.Store.ReadAsync())!.Request!.Overtakes);
        var resumed = fixture.CreateCoordinator();
        await resumed.ServiceAsync(() => fixture.Head, high, true, false);
        Assert.Empty(fixture.Calls);
        await resumed.ServiceAsync(() => fixture.Head, [], true, false);
        await resumed.DrainAsync();
        Assert.Equal(3, Assert.Single(fixture.Calls).Overtakes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Retry_GoalWaitingByOutcomeTime_YieldsUntilGoalIsServed(bool equalTime)
    {
        using var fixture = new SelectedExecutionBackstopFixture();
        var originalTime = fixture.Now;
        await QueueFailedAttempt(fixture);
        var pending = (await fixture.Store.ReadAsync())!.Request!;
        var waiting = GoalAt(equalTime ? fixture.Now : originalTime.AddMinutes(1));
        await fixture.Coordinator.ServiceAsync(() => fixture.Head, [waiting], true, false);
        Assert.Single(fixture.Calls);
        Assert.Equal(1, (await fixture.Store.ReadAsync())!.Request!.Attempts);
        await fixture.Coordinator.ObserveServedAsync([waiting]);
        Assert.Equal(0, (await fixture.Store.ReadAsync())!.Request!.Overtakes);
        await fixture.Finish();
        Assert.Equal(2, fixture.Calls.Count);
        Assert.Equal(pending.Id, fixture.Calls.Last().Id);
        Assert.Equal(2, fixture.Calls.Last().Attempts);
        Assert.False(fixture.Calls.Last().RetryPending);
    }

    [Fact]
    public async Task Retry_NoWaitingGoals_ResumesOnFirstServiceCall()
    {
        using var fixture = new SelectedExecutionBackstopFixture();
        await QueueFailedAttempt(fixture);
        var pending = (await fixture.Store.ReadAsync())!.Request!;
        await fixture.Finish();
        Assert.Equal(2, fixture.Calls.Count);
        Assert.Equal(pending.Id, fixture.Calls.Last().Id);
        Assert.Equal(2, fixture.Calls.Last().Attempts);
        Assert.Null((await fixture.Store.ReadAsync())!.Request);
    }

    [Fact]
    public async Task Retry_ThreeAlreadyWaitingGoals_DoNotCountTowardPromotion()
    {
        using var fixture = new SelectedExecutionBackstopFixture();
        var originalTime = fixture.Now;
        await QueueFailedAttempt(fixture, overtakes: 1);
        var waiting = Enumerable.Range(1, 3)
            .Select(index => GoalAt(originalTime.AddMinutes(index), GoalPriority.High)).ToArray();
        for (var index = 0; index < waiting.Length; index++)
        {
            await fixture.Coordinator.ServiceAsync(() => fixture.Head, waiting[index..], true, false);
            Assert.Single(fixture.Calls);
            await fixture.Coordinator.ObserveServedAsync([waiting[index]]);
            Assert.Equal(0, (await fixture.Store.ReadAsync())!.Request!.Overtakes);
        }
        await fixture.Finish();
        Assert.Equal(2, fixture.Calls.Count);
    }

    [Fact]
    public async Task Retry_PromotedByLaterGoals_StillWaitsForEveryAlreadyWaitingGoal()
    {
        using var fixture = new SelectedExecutionBackstopFixture();
        var originalTime = fixture.Now;
        await QueueFailedAttempt(fixture);
        var waiting = Enumerable.Range(1, 3).Select(index => GoalAt(originalTime.AddMinutes(index))).ToArray();
        var later = Enumerable.Range(1, 3)
            .Select(index => GoalAt(fixture.Now.AddMinutes(index), GoalPriority.High)).ToArray();
        foreach (var goal in later)
        {
            await fixture.Coordinator.ServiceAsync(() => fixture.Head, [.. waiting, goal], true, false);
            Assert.Single(fixture.Calls);
            await fixture.Coordinator.ObserveServedAsync([goal]);
        }
        Assert.Equal(3, (await fixture.Store.ReadAsync())!.Request!.Overtakes);
        for (var index = 0; index < waiting.Length; index++)
        {
            await fixture.Coordinator.ServiceAsync(() => fixture.Head, waiting[index..], true, false);
            Assert.Single(fixture.Calls);
            Assert.Equal(1, (await fixture.Store.ReadAsync())!.Request!.Attempts);
            await fixture.Coordinator.ObserveServedAsync([waiting[index]]);
        }
        await fixture.Finish();
        Assert.Equal(2, fixture.Calls.Count);
        Assert.Equal(3, fixture.Calls.Last().Overtakes);
    }

    [Fact]
    public async Task Retry_FreshStoresAndCoordinator_ReadResetQueuePositionAndYield()
    {
        using var fixture = new SelectedExecutionBackstopFixture();
        var originalTime = fixture.Now;
        await QueueFailedAttempt(fixture, overtakes: 3);
        var events = new SqliteRunEventStore(Path.Combine(fixture.Home, "run-events.db"));
        var store = new SelectedExecutionBackstopStore(events);
        var coordinator = new SelectedExecutionBackstopCoordinator(store, fixture, fixture.Items,
            fixture.Home, () => fixture.Now, fixture.Lines.Enqueue);
        var pending = Assert.IsType<SelectedExecutionBackstopRequest>((await store.ReadAsync())!.Request);
        Assert.Equal(fixture.Now, pending.QueuedAt);
        Assert.Equal(0, pending.Overtakes);
        Assert.Equal(1, pending.Attempts);
        Assert.True(pending.RetryPending);
        await coordinator.ServiceAsync(() => fixture.Head, [GoalAt(originalTime.AddMinutes(1))], true, false);
        await coordinator.DrainAsync();
        Assert.Single(fixture.Calls);
        var persisted = (await new SelectedExecutionBackstopStore(new SqliteRunEventStore(
            Path.Combine(fixture.Home, "run-events.db"))).ReadAsync())!.Request!;
        Assert.Equal(pending.QueuedAt, persisted.QueuedAt);
        Assert.Equal(0, persisted.Overtakes);
        Assert.Equal(1, persisted.Attempts);
    }

    private static async Task QueueFailedAttempt(SelectedExecutionBackstopFixture fixture, int overtakes = 0)
    {
        await fixture.Land();
        await fixture.Service(capacity: false);
        var state = (await fixture.Store.ReadAsync())!;
        await fixture.Store.WriteAsync("fixture-overtakes", state with
            { Request = state.Request! with { Overtakes = overtakes } }, fixture.Now);
        fixture.Run = _ =>
        {
            fixture.Now = fixture.Now.AddHours(1);
            return Task.FromResult(SelectedExecutionBackstopOutcome.Infrastructure("fixture-apparatus-fault"));
        };
        await fixture.Finish();
        Assert.Single(fixture.Calls);
        fixture.Run = _ => Task.FromResult(SelectedExecutionBackstopOutcome.Green);
    }

    private static Goal GoalAt(DateTimeOffset when, GoalPriority priority = GoalPriority.Normal)
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("backstop admission fixture");
        var snapshot = kernel.ExportSnapshot();
        kernel = AgentOrchestratorKernel.FromSnapshot(snapshot with { Goals = snapshot.Goals.Select(item => item with
            { Timeline = item.Timeline.Select(evt => evt with { Kind = ProgressKind.GoalPolicyDecision, Message = "Verified", OccurredAt = when }).ToArray() }).ToArray() });
        goal = kernel.GetGoal(goal.Id);
        kernel.SetGoalPriority(goal.Id, priority, "test", "fixture priority");
        return goal;
    }
}
