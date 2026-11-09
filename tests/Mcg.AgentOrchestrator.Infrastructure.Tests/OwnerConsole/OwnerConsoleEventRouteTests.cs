using Mcg.AgentOrchestrator.App.OwnerConsole;

// Parallel-safe: each route owns its filter, coalescer and coordinator.
public sealed class OwnerConsoleEventRouteTests
{
    [Theory]
    [InlineData("GOAL goal=11111111 result=executed")]
    [InlineData("GOAL goal=11111111 state=Verified result=held")]
    [InlineData("GOAL goal=11111111 state=Verified reason=waiting")]
    [InlineData("GOAL goal=11111111 state=Verified state=Verifying result=held reason=waiting")]
    public async Task IncompleteGoalBetweenHeldEventsInvalidatesThePreviousKey(string intermediate)
    {
        using var fixture = new OwnerConsoleRefreshReuseFixture(marker: false);
        var refresh = fixture.Coordinator();
        var coalescer = new OwnerConsoleRefreshCoalescer((items, scope) => refresh.RefreshAsync(items, scope: scope));
        async Task Route(string detail)
        {
            fixture.Filter.Route(new(fixture.Clock.Now, "goal", "11111111", detail), coalescer);
            await coalescer.Completion;
        }
        const string held = "GOAL goal=11111111 state=Verified result=held reason=waiting";
        await Route(held);
        await Route(intermediate);
        await Route(held);
        Assert.Equal(3, fixture.State.Boards);
        Assert.Equal(3, fixture.Source.Reads);
        await Route(held);
        Assert.Equal(3, fixture.State.Boards);
        Assert.Equal(3, fixture.Source.Reads);
    }

    [Fact]
    public async Task HeartbeatsOnlyReadPanes_RepeatsFoldWithoutRefresh_ChangesRefreshFully()
    {
        using var fixture = new OwnerConsoleRefreshReuseFixture(marker: false);
        var refresh = fixture.Coordinator();
        var coalescer = new OwnerConsoleRefreshCoalescer((items, scope) => refresh.RefreshAsync(items, scope: scope));
        async Task Route(string kind, string? goal, string detail, DateTimeOffset? timestamp = null)
        {
            fixture.Filter.Route(new(timestamp ?? fixture.Clock.Now, kind, goal, detail), coalescer);
            await coalescer.Completion;
        }
        await refresh.RefreshAsync();
        await Route("tick-completed", null, "TICK tick=1");
        await Route("acceptance", "11111111", "ACCEPTANCE goal=11111111 result=running");
        Assert.Equal(1, fixture.State.Boards);
        Assert.Equal(1, fixture.Source.Reads);
        Assert.Equal(3, fixture.GateReads);
        Assert.Equal(3, fixture.TickReads);
        const string held = "GOAL goal=11111111 state=Verified result=held reason=acceptance width 1 reached";
        await Route("goal", "11111111", held);
        Assert.Equal(2, fixture.State.Boards);
        await Route("goal", "22222222", held.Replace("11111111", "22222222"));
        var before = fixture.State.Boards;
        var lastBeforeRepeat = refresh.Last;
        fixture.Clock.Now = fixture.Clock.Now.AddSeconds(1);
        await Route("goal", "11111111", held);
        Assert.Equal(before, fixture.State.Boards);
        Assert.Equal(before, fixture.Source.Reads);
        Assert.Equal(lastBeforeRepeat, refresh.Last); // Suppression requests no refresh.
        await Route("goal", "11111111", held.Replace("width 1", "width 2"), lastBeforeRepeat);
        Assert.Equal(++before, fixture.State.Boards);
        // GOAL records update last activity even though the activity pane does not narrate them.
        Assert.Equal(fixture.Clock.Now, refresh.Last);
        await Route("goal", "11111111", held.Replace("Verified", "Verifying"));
        Assert.Equal(++before, fixture.State.Boards);
        await Route("loop-relaunch", "11111111", "LOOP_RELAUNCH_NOT_REQUIRED");
        Assert.Equal(++before, fixture.State.Boards);
        Assert.Equal(before, fixture.Source.Reads);
    }

    [Theory]
    [InlineData("ACCEPTANCE result=passed")]
    [InlineData("ACCEPTANCE result=failed")]
    [InlineData("ACCEPTANCE")]
    [InlineData("ACCEPTANCE result=running result=failed")]
    public void OnlyPositiveRunningEvidenceSelectsPaneRoute(string detail) =>
        Assert.False(OwnerConsoleEventRoute.IsPaneOnly(new(DateTimeOffset.UnixEpoch, "acceptance", "11111111", detail)));

    [Fact]
    public async Task CoalescedStreamAndHeartbeatRetainFullWorkAndBothTriggers()
    {
        using var fixture = new OwnerConsoleRefreshReuseFixture(marker: false);
        var refresh = fixture.Coordinator();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.State.BeforeLoad = async () =>
        {
            entered.TrySetResult();
            await release.Task;
        };
        var coalescer = new OwnerConsoleRefreshCoalescer((items, scope) => refresh.RefreshAsync(items, scope: scope));
        coalescer.Request(new(fixture.Clock.Now, "goal", "11111111", "GOAL state=Verified result=held reason=first"),
            OwnerConsoleRefreshScope.None);
        try
        {
            await Signal(entered.Task, "first board load entered");
            coalescer.Request(new(fixture.Clock.Now, "tick", null, "TICK"), OwnerConsoleRefreshScope.None);
            coalescer.Request(null, OwnerConsoleRefreshScope.For(["11111111"]));
        }
        finally { release.TrySetResult(); }
        await Signal(coalescer.Completion, "coalescer drained full and pane work");
        Assert.Equal(2, fixture.State.Boards);
        Assert.Equal(2, fixture.Source.Reads);
        Assert.Contains("trigger-conductor-event=2", fixture.TimingLine);
        Assert.Contains("trigger-change-stream=1", fixture.TimingLine);
    }

    private static async Task Signal(Task signal, string name)
    {
        try { await signal.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken); }
        catch (TimeoutException) { Assert.Fail("Signal missing: " + name); }
    }
}
