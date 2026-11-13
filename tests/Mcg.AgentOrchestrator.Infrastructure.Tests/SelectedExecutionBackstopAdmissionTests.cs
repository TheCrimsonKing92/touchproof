using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: all ordering facts and live-gate observations are supplied by the test.
public sealed class SelectedExecutionBackstopAdmissionTests
{
    [Fact]
    public void OrdinaryNormalEntrantUsesWaitingOrderAndExistingOvertakePromotion()
    {
        var queued = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var older = GoalAt(queued.AddMinutes(-1));
        var newer = GoalAt(queued.AddMinutes(1));
        Assert.Equal(new[] { older }, GoalSchedulingOrder.BeforeBackstop([newer, older], queued, 0));
        newer = GoalAt(queued.AddMinutes(1), GoalPriority.High);
        Assert.Equal(new[] { newer, older }, GoalSchedulingOrder.BeforeBackstop([newer, older], queued, 0));
        Assert.Empty(GoalSchedulingOrder.BeforeBackstop([newer, older], queued, 3));
        Assert.Equal(GoalEffectivePriorityIndex.EffectiveLevel(GoalPriority.Normal, 3), GoalPriority.Critical);
    }

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
    public async Task ThreeHigherPriorityOvertakesPersistAndPromoteAcrossRestart()
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
        await resumed.DrainAsync();
        Assert.Equal(3, Assert.Single(fixture.Calls).Overtakes);
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
