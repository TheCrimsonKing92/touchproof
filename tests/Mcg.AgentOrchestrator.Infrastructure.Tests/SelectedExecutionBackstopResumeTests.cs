using Mcg.AgentOrchestrator.App.Orchestration;

// Parallel-safe: a new coordinator adopts fixture-local persisted state; no process restart.
public sealed class SelectedExecutionBackstopResumeTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task RestartAdoptsQueuedOrStartedRequestExactlyOnce(int attempts)
    {
        using var fixture = new SelectedExecutionBackstopFixture();
        await fixture.Store.WriteAsync("queued", new("main-0", Request: new("persisted", "main-0", "main-1", 0, [], fixture.Now)), fixture.Now);
        if (attempts > 0)
            await fixture.Store.WriteAsync("started", (await fixture.Store.ReadAsync())! with
                { Request = (await fixture.Store.ReadAsync())!.Request! with { Attempts = attempts } }, fixture.Now);
        var resumed = fixture.CreateCoordinator();
        await resumed.ServiceAsync(() => "main-2", [], true, false);
        await resumed.DrainAsync();
        await resumed.ServiceAsync(() => "main-2", [], true, false);
        var call = Assert.Single(fixture.Calls);
        Assert.Equal("persisted", call.Id);
        Assert.Equal(attempts == 0 ? "main-2" : "main-1", call.ToCommit);
        Assert.Equal(attempts + 1, call.Attempts);
        Assert.Single((await fixture.Events.ReadByTypeSinceAsync(SelectedExecutionBackstopStore.EventType)).Where(evt => evt.Operation == "queued"));
        Assert.Equal(call.ToCommit, (await fixture.Store.ReadAsync())!.CoveredCommit);
    }
}
