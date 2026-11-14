using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: substituted runner and local stores only.
public sealed class SelectedExecutionBackstopInfrastructureFailureTests
{
    [Theory]
    [InlineData("infrastructure")]
    [InlineData("exception")]
    [InlineData("unmatched-red")]
    public async Task Outcome_InfrastructureFailure_RequeuesSameRequestAtRecordedTime(string result)
    {
        using var fixture = new SelectedExecutionBackstopFixture();
        await fixture.Land();
        await fixture.Service(capacity: false);
        var queued = (await fixture.Store.ReadAsync())!.Request!;
        await fixture.Store.WriteAsync("fixture-overtakes", (await fixture.Store.ReadAsync())! with
            { Request = queued with { Overtakes = 2 } }, fixture.Now);
        var outcomeAt = fixture.Now.AddHours(3);
        fixture.Run = _ =>
        {
            fixture.Now = outcomeAt;
            return result switch
            {
                "exception" => throw new IOException("fixture-runner-fault"),
                "unmatched-red" => Task.FromResult(new SelectedExecutionBackstopOutcome("red", ["Unselected.Test.Fails"], ["lane"])),
                _ => Task.FromResult(SelectedExecutionBackstopOutcome.Infrastructure("fixture-apparatus-fault"))
            };
        };
        // Advance on every read: separate reads for queue time and event time must disagree.
        var coordinator = new SelectedExecutionBackstopCoordinator(fixture.Store, fixture, fixture.Items, fixture.Home,
            () => { var now = fixture.Now; fixture.Now = now.AddTicks(1); return now; }, fixture.Lines.Enqueue);
        await coordinator.ServiceAsync(() => fixture.Head, [], true, false);
        await coordinator.DrainAsync();
        var started = Assert.Single(fixture.Calls);
        Assert.Equal(2, started.Overtakes);
        var state = Assert.IsType<SelectedExecutionBackstopState>(await fixture.Store.ReadAsync());
        var pending = Assert.IsType<SelectedExecutionBackstopRequest>(state.Request);
        var recorded = Assert.Single((await fixture.Events.ReadByTypeSinceAsync(SelectedExecutionBackstopStore.EventType))
            .Where(evt => evt.Operation == "infrastructure-failure"));
        Assert.Equal(outcomeAt, pending.QueuedAt);
        Assert.Equal(recorded.OccurredAt, pending.QueuedAt);
        Assert.Equal(0, pending.Overtakes);
        Assert.True(pending.RetryPending);
        Assert.Equal(started.Id, pending.Id);
        Assert.Equal(1, pending.Attempts);
        Assert.Equal(started.Attempts, pending.Attempts);
        Assert.Equal(started.FromCommit, pending.FromCommit);
        Assert.Equal(started.ToCommit, pending.ToCommit);
        Assert.Equal(started.ThroughOrdinal, pending.ThroughOrdinal);
        Assert.Equal(started.GoalIds, pending.GoalIds);
        Assert.Null(state.OpenMiss);
        Assert.Null(state.CoveredCommit);
        Assert.Empty(await fixture.Items.ListAsync());
        Assert.Null(await AcceptanceSelectedExecutionSessionSuspension.ReadAsync("unused", fixture.Home));
    }

    [Fact]
    public async Task PendingRetry_NewerLanding_CoalescesWithoutLosingAttemptOrQueuePosition()
    {
        using var fixture = new SelectedExecutionBackstopFixture();
        fixture.Head = "main-1";
        fixture.Run = _ =>
        {
            fixture.Now = fixture.Now.AddHours(1);
            return Task.FromResult(SelectedExecutionBackstopOutcome.Infrastructure("fixture-apparatus-fault"));
        };
        await fixture.Land();
        await fixture.Finish();
        var retry = (await fixture.Store.ReadAsync())!.Request!;
        fixture.Now = fixture.Now.AddMinutes(1);
        await fixture.Land("goal-2", "main-2");
        fixture.Head = "main-2";
        await fixture.Service(capacity: false);
        var coalesced = (await fixture.Store.ReadAsync())!.Request!;
        Assert.Equal(retry.Id, coalesced.Id);
        Assert.Equal(retry.FromCommit, coalesced.FromCommit);
        Assert.Equal("main-2", coalesced.ToCommit);
        Assert.Equal(2, coalesced.ThroughOrdinal);
        Assert.Equal(new[] { "goal-1", "goal-2" }, coalesced.GoalIds);
        Assert.Equal(retry.QueuedAt, coalesced.QueuedAt);
        Assert.Equal(retry.Overtakes, coalesced.Overtakes);
        Assert.Equal(1, coalesced.Attempts);
        Assert.True(coalesced.RetryPending);
        Assert.Single(fixture.Calls);
        fixture.Run = _ => Task.FromResult(SelectedExecutionBackstopOutcome.Green);
        await fixture.Finish();
        var resumed = fixture.Calls.Last();
        Assert.Equal(2, fixture.Calls.Count);
        Assert.Equal(coalesced.Id, resumed.Id);
        Assert.Equal(coalesced.FromCommit, resumed.FromCommit);
        Assert.Equal(coalesced.ToCommit, resumed.ToCommit);
        Assert.Equal(coalesced.ThroughOrdinal, resumed.ThroughOrdinal);
        Assert.Equal(coalesced.GoalIds, resumed.GoalIds);
        Assert.Equal(coalesced.QueuedAt, resumed.QueuedAt);
        Assert.Equal(coalesced.Overtakes, resumed.Overtakes);
        Assert.Equal(2, resumed.Attempts);
        Assert.False(resumed.RetryPending);
        Assert.Single((await fixture.Events.ReadByTypeSinceAsync(SelectedExecutionBackstopStore.EventType))
            .Where(evt => evt.Operation == "queued"));
    }

    [Fact]
    public void Request_OlderSnapshotWithoutRetryMarker_RemainsAnInterruptedAttempt()
    {
        var request = JsonSerializer.Deserialize<SelectedExecutionBackstopRequest>("""
            {"Id":"old","FromCommit":"main-0","ToCommit":"main-1","ThroughOrdinal":1,
             "GoalIds":["goal-1"],"QueuedAt":"2026-01-01T00:00:00Z","Overtakes":2,"Attempts":1}
            """);
        Assert.NotNull(request);
        Assert.False(request.RetryPending);
        Assert.Equal(1, request.Attempts);
        Assert.Equal(2, request.Overtakes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InfrastructureFailureKeepsOneRequestAndDoesNotOpenMiss(bool throws)
    {
        using var fixture = new SelectedExecutionBackstopFixture();
        fixture.Run = _ => throws ? throw new IOException("fixture-runner-fault") :
            Task.FromResult(SelectedExecutionBackstopOutcome.Infrastructure("fixture-apparatus-fault"));
        await fixture.Land();
        await fixture.Finish();
        var state = Assert.IsType<SelectedExecutionBackstopState>(await fixture.Store.ReadAsync());
        Assert.Null(state.OpenMiss);
        Assert.Null(state.CoveredCommit);
        Assert.Single(state.Uncovered);
        Assert.Empty(await fixture.Items.ListAsync());
        Assert.Null(await AcceptanceSelectedExecutionSessionSuspension.ReadAsync("unused", fixture.Home));
        Assert.Contains(fixture.Lines, line => line.Contains("result=infrastructure-failure"));
        var pending = Assert.IsType<SelectedExecutionBackstopRequest>(state.Request);
        fixture.Run = _ => Task.FromResult(SelectedExecutionBackstopOutcome.Green);
        await fixture.Finish();
        Assert.Equal(new[] { pending.Id, pending.Id }, fixture.Calls.Select(call => call.Id));
        Assert.Single((await fixture.Events.ReadByTypeSinceAsync(SelectedExecutionBackstopStore.EventType)).Where(evt => evt.Operation == "queued"));
    }

    [Fact]
    public void GateClassifierKeepsApparatusSeparateFromCompletedRedAndNonTestFailures()
    {
        var infrastructure = new AcceptanceCheckResult("lane", false, 1, "apparatus", FailureClassification: AcceptanceFailureClassifications.GateEnvironmentInterference);
        Assert.Equal("infrastructure-failure", SelectedExecutionBackstopGateRunner.Classify(new(false, false, 1, "", Checks: [infrastructure])).Result);
        var red = infrastructure with { FailureClassification = null, FailingTestIdentities = ["Tests.Broken"] };
        var miss = SelectedExecutionBackstopGateRunner.Classify(new(false, false, 1, "", Checks: [red]));
        Assert.Equal("red", miss.Result);
        Assert.Equal(new[] { "Tests.Broken" }, miss.FailingTests);
        Assert.Equal("red", SelectedExecutionBackstopGateRunner.Classify(new(false, false, 1, "", Checks: [red with { FailingTestIdentities = null }])).Result);
        Assert.Equal("infrastructure-failure", SelectedExecutionBackstopGateRunner.Classify(new(false, true, null, "not run")).Result);
    }

    [Theory]
    [InlineData(AcceptanceFailureClassifications.BuildPhaseTimeout)]
    [InlineData(AcceptanceFailureClassifications.MissingTool)]
    [InlineData(AcceptanceFailureClassifications.SeededRepositoryApparatus)]
    public void EveryExistingApparatusClassificationRemainsInfrastructure(string classification)
    {
        var failed = new AcceptanceCheckResult("check", false, 1, classification, FailureClassification: classification);
        Assert.Equal("infrastructure-failure", SelectedExecutionBackstopGateRunner.Classify(new(false, false, 1, "", Checks: [failed])).Result);
    }
}
