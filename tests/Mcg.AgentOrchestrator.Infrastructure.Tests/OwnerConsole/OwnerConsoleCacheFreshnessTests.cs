using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Core;

// Parallel-safe: fake wall time and event-gated reads; no elapsed-time verdicts.
public sealed class OwnerConsoleCacheFreshnessTests
{
    [Fact]
    public async Task UnchangedSourcesRefreshAgesGateTextAndTerminalExpiryAtCurrentTime()
    {
        using var f = new OwnerConsoleRefreshReuseFixture(boardReuse: true);
        var snapshot = f.Harness.Kernel.ExportSnapshot();
        f.Harness.Kernel.ReplaceWithSnapshot(snapshot with { Goals = snapshot.Goals.Select(goal => goal.Id == "22222222"
            ? goal with { Status = GoalStatus.Failed, Timeline = [new(goal.Id, null, ProgressKind.GoalPolicyDecision,
                "failed", f.Clock.Now.AddHours(-24).AddMinutes(1))] } : goal).ToArray() });
        f.State.Versions["22222222"] = f.Clock.Now.AddHours(-24).AddMinutes(1).ToString("O");
        var first = await f.Builder.BuildBoardAsync();
        Assert.Equal(2, first.Board.Length);
        f.Clock.Now = f.Clock.Now.AddMinutes(2);
        var next = await f.Builder.BuildBoardAsync();
        var row = Assert.Single(next.Board);
        Assert.Equal("11111111", row.GoalId);
        Assert.Equal(TimeSpan.FromMinutes(4), row.Age);
        Assert.Equal("gate running 4m", row.Stage);
        Assert.Equal(1, f.State.Boards);
        Assert.Equal(2, f.GateReads);
        Assert.Equal(2, f.TickReads);
    }

    [Fact]
    public async Task MutationDuringReadCannotCertifyOldBoardWithNewToken()
    {
        using var f = new OwnerConsoleRefreshReuseFixture(boardReuse: true);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.State.AfterLoad = async () => { entered.TrySetResult(); await release.Task; };
        using var generation = f.Snapshots.BeginRefresh(reuseUnchanged: true);
        var reading = f.Builder.BuildBoardAsync();
        try
        {
            await TestHangGuard.WaitAsync(entered.Task, "board snapshot captured before concurrent mutation");
            var snapshot = f.Harness.Kernel.ExportSnapshot();
            f.Harness.Kernel.ReplaceWithSnapshot(snapshot with { Goals = snapshot.Goals.Select(goal => goal.Id == "11111111"
                ? goal with { Objective = "Updated during read" } : goal).ToArray() });
            f.BoardMarker = "state:2";
        }
        finally { release.TrySetResult(); }
        var old = await TestHangGuard.WaitAsync(reading, "first board read released");
        Assert.Contains(old.Board, row => row.GoalId == "11111111" && row.Title == "First");
        var next = await f.Builder.BuildBoardAsync();
        Assert.Contains(next.Board, row => row.GoalId == "11111111" && row.Title == "Updated during read");
        await f.Builder.BuildBoardAsync();
        Assert.Equal(2, f.State.Boards);
        Assert.Equal(2, f.State.Loads["11111111"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MutationDuringQuestionReadAndForcedInvalidationAreNotAcknowledgedByOldRead(bool changeToken)
    {
        using var f = new OwnerConsoleRefreshReuseFixture();
        var source = new OwnerQuestionReadModel(f.State, f.Root, changeMarker: () => f.Marker);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.State.AfterLoad = async () => { entered.TrySetResult(); await release.Task; };
        var reading = source.ReadAsync(CancellationToken.None);
        try
        {
            await TestHangGuard.WaitAsync(entered.Task, "question snapshot captured before invalidation");
            var snapshot = f.Harness.Kernel.ExportSnapshot();
            f.Harness.Kernel.ReplaceWithSnapshot(snapshot with { Goals = snapshot.Goals.Select(goal => goal.Id == "11111111"
                ? goal with { CurrentHold = new("new-question", "steward-owner-question", "New decision", f.Clock.Now) }
                : goal).ToArray() });
            if (changeToken) f.Marker = "state:changed";
            source.Invalidate();
        }
        finally { release.TrySetResult(); }
        var old = await TestHangGuard.WaitAsync(reading, "question read released");
        var next = await source.ReadAsync(CancellationToken.None);
        Assert.Empty(old.Live);
        Assert.Equal("New decision", Assert.Single(next.Live).Text);
        Assert.NotSame(old, next);
        Assert.Same(next, await source.ReadAsync(CancellationToken.None));
        Assert.Equal(2, f.State.RequestReads);
    }
}
