using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: event gates and state are owned by each test invocation.
public sealed class OwnerConsoleSnapshotReservationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InFlightLoadCannotUseVersionsObservedAfterItsReservation(bool observeFirst)
    {
        using var fixture = new OwnerConsoleRefreshReuseFixture();
        var captured = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var snapshots = new OwnerConsoleRefreshGoalSnapshots(new Query(fixture.State, captured, release));
        var token = TestContext.Current.CancellationToken;
        using (snapshots.BeginRefresh(reuseUnchanged: true))
        {
            if (observeFirst) snapshots.Observe(await fixture.State.ListGoalMetadataAsync(token));
            var first = snapshots.LoadAsync([new GoalId("11111111")], token);
            try
            {
                await captured.Task.WaitAsync(TimeSpan.FromSeconds(30), token);
                var snapshot = fixture.Harness.Kernel.ExportSnapshot();
                fixture.Harness.Kernel.ReplaceWithSnapshot(snapshot with
                {
                    Goals = snapshot.Goals.Select(goal => goal.Id == "11111111"
                        ? goal with { Objective = "Changed" } : goal).ToArray()
                });
                fixture.State.Versions["11111111"] = "b";
                snapshots.Observe(await fixture.State.ListGoalMetadataAsync(token));
            }
            finally { release.TrySetResult(); }
            Assert.Equal("First", Assert.Single((await first.WaitAsync(TimeSpan.FromSeconds(30), token)).Goals).Objective);
        }
        using (snapshots.BeginRefresh(reuseUnchanged: true))
        {
            snapshots.Observe(await fixture.State.ListGoalMetadataAsync(token));
            var second = await snapshots.LoadAsync([new GoalId("11111111")], token);
            Assert.Equal("Changed", Assert.Single(second.Goals).Objective);
        }
        Assert.Equal(2, fixture.State.Loads["11111111"]);
        Assert.Equal(2, snapshots.StoreLoadedGoals);
    }

    private sealed class Query(OwnerConsoleRefreshReuseFixture.Query state,
        TaskCompletionSource captured, TaskCompletionSource release) : IOrchestratorStateQueries
    {
        private bool _held;
        public Task<IReadOnlyList<GoalSummary>> ListGoalMetadataAsync(CancellationToken token = default) =>
            state.ListGoalMetadataAsync(token);

        public async Task<AgentOrchestratorKernel> LoadGoalsAsync(IReadOnlyCollection<GoalId> ids,
            CancellationToken cancellationToken = default)
        {
            var loaded = await state.LoadGoalsAsync(ids, cancellationToken);
            if (!_held)
            {
                _held = true;
                captured.TrySetResult();
                await release.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
            }
            return loaded;
        }
    }
}
