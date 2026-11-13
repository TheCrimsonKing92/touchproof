using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: fixture-local stores, no external gate.
public sealed class SelectedExecutionBackstopMissTests
{
    [Fact]
    public async Task MatchingRedCarriesFullRangeRaisesOneItemAndEmitsOneRedLine()
    {
        using var fixture = new SelectedExecutionBackstopFixture();
        fixture.Run = _ => Task.FromResult(new SelectedExecutionBackstopOutcome("red", ["Candidate.X.First", "Tests.Second"], ["lane"]));
        await fixture.Land();
        await fixture.Land("full-gate-goal", "main-2", AcceptanceSelectedExecutionRecord.Disabled);
        await fixture.Finish();
        await fixture.Service();
        var miss = Assert.IsType<SelectedExecutionBackstopMissRecord>((await fixture.Store.ReadAsync())!.OpenMiss);
        Assert.Equal("main-0", miss.CoveredFromCommit);
        Assert.Equal("main-2", miss.CoveredToCommit);
        Assert.Equal(new[] { "Candidate.X.First", "Tests.Second" }, miss.FailingTests);
        Assert.Equal(new[] { "lane" }, miss.FailedChecks);
        Assert.Equal(new[] { "goal-1", "full-gate-goal" }, miss.GoalIds);
        Assert.Null(miss.GoalRangeNote);
        Assert.Null((await fixture.Store.ReadAsync())!.Request);
        var item = Assert.Single(await fixture.Items.ListAsync());
        Assert.Equal(miss.Id, JsonSerializer.Deserialize<SelectedExecutionBackstopMissRecord>(item.Body)!.Id);
        var line = Assert.Single(fixture.Lines.Where(line => line.Contains("result=red")));
        Assert.Contains("from=main-0 to=main-2", line);
        Assert.Contains("failingTests=2", line);
        Assert.Contains("goals=goal-1,full-gate-goal", line);
        Assert.Single(fixture.Calls);
        Assert.Equal(miss.Id, (await AcceptanceSelectedExecutionSessionSuspension.ReadAsync("unused", fixture.Home))!.MissId);
    }

    [Theory]
    [InlineData("outside")]
    [InlineData("no-identities")]
    [InlineData("no-landings")]
    public async Task UnmatchedRedRetainsRequestWithoutMissOrSuspension(string evidence)
    {
        using var fixture = new SelectedExecutionBackstopFixture();
        string[] failing = evidence == "no-identities" ? [] : [" Other.X.First(\"a.b\") ", "unresolvable"];
        fixture.Run = _ => Task.FromResult(new SelectedExecutionBackstopOutcome("red", failing, ["lane"]));
        if (evidence == "no-landings")
            await fixture.Store.WriteAsync("queued", new("main-0", Request: new("seed", "main-0", "main-2", 0, [], fixture.Now)), fixture.Now);
        else await fixture.Land();
        await fixture.Service(capacity: false);
        var queued = (await fixture.Store.ReadAsync())!.Request!;
        await fixture.Finish();
        var state = (await fixture.Store.ReadAsync())!;
        var pending = Assert.IsType<SelectedExecutionBackstopRequest>(state.Request);
        Assert.Equal(queued.Id, pending.Id);
        Assert.Equal(queued.FromCommit, pending.FromCommit);
        Assert.Equal(queued.ToCommit, pending.ToCommit);
        Assert.Equal(queued.ThroughOrdinal, pending.ThroughOrdinal);
        Assert.Equal(queued.GoalIds, pending.GoalIds);
        Assert.Equal(queued.QueuedAt, pending.QueuedAt);
        Assert.Equal(queued.Overtakes, pending.Overtakes);
        Assert.Equal(1, pending.Attempts);
        Assert.Null(state.OpenMiss);
        Assert.Empty(await fixture.Items.ListAsync());
        Assert.False(File.Exists(AcceptanceSelectedExecutionSessionSuspension.MarkerPath(fixture.Home)));
        var line = Assert.Single(fixture.Lines.Where(line => line.Contains("result=infrastructure-failure ")));
        Assert.Contains("failingTests=" + failing.Length, line);
        Assert.Contains(evidence == "no-identities" ? "no failing test identities were available"
            : "unmatched=" + string.Join("; ", failing), line);
        Assert.DoesNotContain(await fixture.Events.ReadByTypeSinceAsync(SelectedExecutionBackstopStore.EventType),
            evt => evt.Operation == "red");
        await fixture.Finish();
        Assert.Equal(new[] { queued.Id, queued.Id }, fixture.Calls.Select(call => call.Id));
        Assert.Null((await fixture.Store.ReadAsync())!.OpenMiss);
    }

    [Fact(Timeout = TestHangGuard.WholeTestMilliseconds)]
    public async Task LandingOutsideRequestCannotEstablishMiss()
    {
        using var fixture = new SelectedExecutionBackstopFixture();
        var release = new TaskCompletionSource<SelectedExecutionBackstopOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Run = _ => release.Task;
        await fixture.Land(record: SelectedExecutionBackstopFixture.Applied with
            { Lanes = [new("lane", "skipped", [], ["Covered.C"], null)] });
        await fixture.Service();
        try
        {
            await fixture.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await fixture.Land("later", "main-3");
        }
        finally
        {
            release.TrySetResult(new("red", ["Candidate.X.Method"], ["lane"]));
            await fixture.Coordinator.DrainAsync();
        }
        var state = (await fixture.Store.ReadAsync())!;
        Assert.NotNull(state.Request);
        Assert.Equal(Assert.Single(fixture.Calls).Id, state.Request.Id);
        Assert.Null(state.OpenMiss);
        Assert.Empty(await fixture.Items.ListAsync());
        Assert.False(File.Exists(AcceptanceSelectedExecutionSessionSuspension.MarkerPath(fixture.Home)));
        Assert.Contains(fixture.Lines, line => line.Contains("result=infrastructure-failure "));
    }
}
