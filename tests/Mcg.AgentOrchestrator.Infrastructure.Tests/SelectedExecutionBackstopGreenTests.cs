using Mcg.AgentOrchestrator.App.Orchestration;

// Parallel-safe: fixture-local state only.
public sealed class SelectedExecutionBackstopGreenTests
{
    [Fact]
    public async Task GreenLeavesNoUncoveredLandingAndSurvivesStoreRecreation()
    {
        using var fixture = new SelectedExecutionBackstopFixture();
        await fixture.Land();
        await fixture.Land("goal-2", "main-2");
        await fixture.Finish();
        var recreated = new SelectedExecutionBackstopStore(fixture.Events);
        var state = (await recreated.ReadAsync())!;
        Assert.Equal("main-2", state.CoveredCommit);
        Assert.Empty(state.Uncovered);
        Assert.Null(state.OpenMiss);
        Assert.Null(state.Request);
    }

    [Fact]
    public async Task GreenPersistsCoveredCommitAndClearsOnlyCapturedRange()
    {
        using var fixture = new SelectedExecutionBackstopFixture();
        var release = new TaskCompletionSource<SelectedExecutionBackstopOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Run = _ => release.Task;
        await fixture.Land();
        await fixture.Land("goal-2", "main-2");
        await fixture.Service();
        try
        {
            await fixture.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30)); // Hang-only: runner never entered.
            await fixture.Land("goal-3", "main-3");
        }
        finally { release.TrySetResult(SelectedExecutionBackstopOutcome.Green); await fixture.Coordinator.DrainAsync(); }
        var state = Assert.IsType<SelectedExecutionBackstopState>(await fixture.Store.ReadAsync());
        Assert.Equal("main-2", state.CoveredCommit);
        Assert.Null(state.Request);
        Assert.Equal("goal-3", Assert.Single(state.Uncovered).GoalId);
        Assert.Equal("green", (await fixture.Events.ReadByTypeSinceAsync(SelectedExecutionBackstopStore.EventType))[^1].Operation);
    }
}
