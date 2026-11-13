using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: isolated fixture stores and a substituted in-process runner.
public sealed class SelectedExecutionBackstopCoalescingTests
{
    [Fact]
    public async Task WaitingRequestCoalescesWithoutLosingItsAdmissionPlace()
    {
        using var fixture = new SelectedExecutionBackstopFixture();
        fixture.Head = "main-1";
        await fixture.Land();
        await fixture.Service(capacity: false);
        var queued = (await fixture.Store.ReadAsync())!.Request!;
        fixture.Now = fixture.Now.AddMinutes(1);
        await fixture.Land("goal-2", "main-2");
        fixture.Head = "main-2";
        await fixture.Finish();
        var call = Assert.Single(fixture.Calls);
        Assert.Equal(queued.Id, call.Id);
        Assert.Equal(queued.QueuedAt, call.QueuedAt);
        Assert.Equal("main-2", call.ToCommit);
        Assert.Equal(new[] { "goal-1", "goal-2" }, call.GoalIds);
        Assert.Single((await fixture.Events.ReadByTypeSinceAsync(SelectedExecutionBackstopStore.EventType)).Where(evt => evt.Operation == "queued"));
    }

    [Fact]
    public async Task LandingsCoalesceAtNewestHeadAndDuringRunWaitForNextRequest()
    {
        using var fixture = new SelectedExecutionBackstopFixture();
        var release = new TaskCompletionSource<SelectedExecutionBackstopOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Run = _ => release.Task;
        await fixture.Land();
        await fixture.Land("goal-2", "main-2");
        await fixture.Service();
        try
        {
            await fixture.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30)); // Hang-only: runner entry was not observed.
            await fixture.Land("goal-3", "main-3");
            fixture.Head = "main-3";
            await fixture.Service();
            var call = Assert.Single(fixture.Calls);
            Assert.Equal("main-2", call.ToCommit);
            Assert.Equal(new[] { "goal-1", "goal-2" }, call.GoalIds);
            Assert.Single((await fixture.Events.ReadByTypeSinceAsync(SelectedExecutionBackstopStore.EventType)).Where(evt => evt.Operation == "queued"));
        }
        finally { release.TrySetResult(SelectedExecutionBackstopOutcome.Green); await fixture.Coordinator.DrainAsync(); }
        await fixture.Finish();
        Assert.Equal(new[] { "main-2", "main-3" }, fixture.Calls.Select(call => call.ToCommit));
        Assert.Equal(2, (await fixture.Events.ReadByTypeSinceAsync(SelectedExecutionBackstopStore.EventType)).Count(evt => evt.Operation == "queued"));
    }

    [Fact]
    public async Task NonAppliedGateDoesNotQueueAndReplayedCoveredLandingDoesNotQueueAgain()
    {
        using var fixture = new SelectedExecutionBackstopFixture();
        await fixture.Land(record: AcceptanceSelectedExecutionRecord.Disabled);
        await fixture.Finish();
        Assert.Empty(fixture.Calls);
        Assert.Empty((await fixture.Store.ReadAsync())!.Uncovered);
        Assert.DoesNotContain(fixture.Lines, line => line.Contains("result=queued"));
        await fixture.Land("selected", "main-2");
        await fixture.Finish();
        Assert.Single(fixture.Calls);
        await fixture.Land("selected", "main-2");
        await fixture.Finish();
        Assert.Single(fixture.Calls);
    }
}
