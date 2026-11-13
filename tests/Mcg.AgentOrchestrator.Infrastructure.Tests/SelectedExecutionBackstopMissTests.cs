using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: fixture-local stores, no external gate.
public sealed class SelectedExecutionBackstopMissTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RedCarriesFullRangeRaisesOneItemAndEmitsOneRedLine(bool zeroGoals)
    {
        using var fixture = new SelectedExecutionBackstopFixture();
        fixture.Run = _ => Task.FromResult(new SelectedExecutionBackstopOutcome("red", ["Tests.First", "Tests.Second"], ["lane"]));
        if (zeroGoals)
            await fixture.Store.WriteAsync("queued", new("main-0", Request: new("seed", "main-0", "main-2", 0, [], fixture.Now)), fixture.Now);
        else
        {
            await fixture.Land();
            await fixture.Land("full-gate-goal", "main-2", AcceptanceSelectedExecutionRecord.Disabled);
        }
        await fixture.Finish();
        await fixture.Service();
        var miss = Assert.IsType<SelectedExecutionBackstopMissRecord>((await fixture.Store.ReadAsync())!.OpenMiss);
        Assert.Equal("main-0", miss.CoveredFromCommit);
        Assert.Equal("main-2", miss.CoveredToCommit);
        Assert.Equal(new[] { "Tests.First", "Tests.Second" }, miss.FailingTests);
        Assert.Equal(new[] { "lane" }, miss.FailedChecks);
        Assert.Equal(zeroGoals ? Array.Empty<string>() : ["goal-1", "full-gate-goal"], miss.GoalIds);
        Assert.Equal(zeroGoals ? "no-selected-execution-landing-recorded-in-range" : null, miss.GoalRangeNote);
        var item = Assert.Single(await fixture.Items.ListAsync());
        Assert.Equal(miss.Id, JsonSerializer.Deserialize<SelectedExecutionBackstopMissRecord>(item.Body)!.Id);
        var line = Assert.Single(fixture.Lines.Where(line => line.Contains("result=red")));
        Assert.Contains("from=main-0 to=main-2", line);
        Assert.Contains("failingTests=2", line);
        Assert.Contains(zeroGoals ? "goals=none" : "goals=goal-1,full-gate-goal", line);
        Assert.Single(fixture.Calls);
        Assert.Equal(miss.Id, (await AcceptanceSelectedExecutionSessionSuspension.ReadAsync("unused", fixture.Home))!.MissId);
    }
}
