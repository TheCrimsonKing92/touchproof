using Mcg.AgentOrchestrator.App.OwnerConsole;
using Terminal.Gui.App;

// Parallel-safe: each test owns its headless application, state, clock and gate.
public sealed class OwnerRefreshCoordinatorTests
{
    [Fact]
    public async Task RefreshAsync_QueuedDuringStartup_WaitsUntilReleased()
    {
        using var fixture = new Fixture();
        fixture.Refresh.HoldForStartup();
        var queued = fixture.Refresh.RefreshAsync();
        // test-design-discipline: allow-negative-wait - only this test can release the held startup gate.
        Assert.False(queued.IsCompleted);
        Assert.Equal(0, fixture.Reads);

        fixture.Refresh.Release();
        await Finished(queued);

        Assert.Equal(1, fixture.Reads);
        Assert.Equal(1, fixture.Renders);
        Assert.Equal(1, fixture.TimingWrites);
    }

    [Fact]
    public async Task RefreshAsync_SkipDuringStartup_ReturnsWithoutRunningOrReleasing()
    {
        using var fixture = new Fixture();
        fixture.Refresh.HoldForStartup();

        await Finished(fixture.Refresh.RefreshAsync(skipIfBusy: true));

        Assert.Equal(0, fixture.Reads);
        Assert.Equal(0, fixture.TimingWrites);
        var queued = fixture.Refresh.RefreshAsync();
        // test-design-discipline: allow-negative-wait - no producer can release this test's startup gate.
        Assert.False(queued.IsCompleted);
        fixture.Refresh.Release();
        await Finished(queued);
        Assert.Equal(1, fixture.Reads);
    }

    [Fact]
    public async Task RefreshAsync_LocalDayChanges_ResetsLandingCountAndIdentities()
    {
        using var fixture = new Fixture();
        fixture.Clock.Zone = TimeZoneInfo.CreateCustomTimeZone("test-minus-six", TimeSpan.FromHours(-6), "test", "test");
        fixture.Clock.Now = new(2026, 1, 2, 5, 59, 0, TimeSpan.Zero);
        var landing = Landing(fixture.Clock.Now);
        await Finished(fixture.Refresh.RefreshAsync([landing, landing]));
        Assert.Equal(1, fixture.Refresh.Landings);
        Assert.Equal(landing, Assert.Single(fixture.Refresh.CountedLandings));

        fixture.Clock.Now = fixture.Clock.Now.AddMinutes(2); // Local midnight, with the UTC day unchanged.
        await Finished(fixture.Refresh.RefreshAsync());

        Assert.Equal(new DateTime(2026, 1, 2), fixture.Refresh.LandingDate);
        Assert.Equal(0, fixture.Refresh.Landings);
        Assert.Empty(fixture.Refresh.CountedLandings);
        var today = Landing(fixture.Clock.Now);
        await Finished(fixture.Refresh.RefreshAsync([landing, today, today]));
        Assert.Equal(1, fixture.Refresh.Landings);
        Assert.Equal(today, Assert.Single(fixture.Refresh.CountedLandings));
    }

    [Fact]
    public async Task RefreshAsync_ShutdownDuringStep_SwallowsCancellationAndReleasesGate()
    {
        using var shutdown = new CancellationTokenSource();
        using var fixture = new Fixture(shutdown.Token);
        fixture.ReadLifecycle = () =>
        {
            shutdown.Cancel();
            throw new OperationCanceledException(shutdown.Token);
        };

        await Finished(fixture.Refresh.RefreshAsync());

        Assert.Equal(1, fixture.Reads);
        Assert.Equal(1, fixture.TimingWrites);
        Assert.Empty(fixture.View.NoticeStrip.Visible);
        Assert.Throws<SemaphoreFullException>(() => fixture.Refresh.Release());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RefreshAsync_CancelledBeforeAcquire_PropagatesWithoutReleasing(bool skip)
    {
        using var shutdown = new CancellationTokenSource();
        shutdown.Cancel();
        using var fixture = new Fixture(shutdown.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Refresh.RefreshAsync(skipIfBusy: skip));

        Assert.Equal(0, fixture.Reads);
        Assert.Equal(0, fixture.TimingWrites);
        Assert.Throws<SemaphoreFullException>(() => fixture.Refresh.Release());
    }

    [Fact]
    public async Task RefreshAsync_StepFails_ShowsFailureAndAllowsNextRefresh()
    {
        using var fixture = new Fixture();
        fixture.ReadLifecycle = () => throw new InvalidOperationException("boom");

        await Finished(fixture.Refresh.RefreshAsync());

        var notice = Assert.Single(fixture.View.NoticeStrip.Visible);
        Assert.Equal("refresh failed: boom", notice.Text);
        Assert.Equal(OwnerConsoleNoticeSeverity.Failure, notice.Severity);
        Assert.Equal(OwnerConsoleNoticeSource.Refresh, notice.Source);
        Assert.Equal(1, fixture.Reads);
        Assert.Equal(1, fixture.TimingWrites);
        fixture.ReadLifecycle = () => [];
        await Finished(fixture.Refresh.RefreshAsync(skipIfBusy: true));
        Assert.Equal(2, fixture.Reads);
        Assert.Equal(1, fixture.Renders);
        Assert.Equal(2, fixture.TimingWrites);
    }

    [Fact]
    public async Task Seed_LoadedHistory_PreservesActivityAndDeduplicatesLaterLandings()
    {
        using var fixture = new Fixture();
        var landing = Landing(fixture.Clock.Now.AddMinutes(-2));
        var activity = new OwnerConductEvent(fixture.Clock.Now.AddMinutes(-1), "acceptance", "11111111", "result=passed");
        fixture.Refresh.HoldForStartup();

        fixture.Refresh.Seed(new([landing, activity], activity.Timestamp, 1));

        Assert.Equal(new[] { landing, activity }.Select(fixture.Builder.EnrichEvent), fixture.Refresh.Recent);
        Assert.Equal(activity.Timestamp, fixture.Refresh.Last);
        Assert.Equal(1, fixture.Refresh.Landings);
        Assert.Equal(landing, Assert.Single(fixture.Refresh.CountedLandings));
        Assert.Equal(0, fixture.Reads);
        fixture.Refresh.Release();
        var next = Landing(fixture.Clock.Now);
        await Finished(fixture.Refresh.RefreshAsync([landing, next]));

        Assert.Equal(2, fixture.Refresh.Landings);
        Assert.Equal(next.Timestamp, fixture.Refresh.Last);
        Assert.Equal(new[] { landing, activity, next }, fixture.Refresh.Recent);
        Assert.Equal(new[] { landing, next }, fixture.Refresh.CountedLandings.OrderBy(item => item.Timestamp));
        Assert.Equal(1, fixture.Renders);
    }

    [Fact]
    public async Task CanceledRefresh_DrainsUnderlyingReadBeforeDisposal()
    {
        using var stop = new CancellationTokenSource();
        using var fixture = new Fixture(stop.Token);
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var disposed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observed = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        var error = new IOException("late lifecycle read failure");
        fixture.ReadLifecycle = () =>
        {
            entered.SetResult();
            release.Wait(); // An uncooperative synchronous source, released only by this test.
            throw error;
        };
        var clock = new ManualStewardTimeProvider();
        var disposals = 0;
        var refresh = fixture.Refresh.RefreshAsync();
        Task? shutdown = null;
        try
        {
            await Finished(entered.Task);
            stop.Cancel();
            await Finished(refresh);
            shutdown = OwnerConsoleScreenOperation.DrainAsync(refresh, () => fixture.Refresh.Completion, () =>
            {
                Interlocked.Increment(ref disposals);
                disposed.SetResult();
                return ValueTask.CompletedTask;
            }, clock, ex => observed.TrySetResult(ex));
            await Finished(clock.TimerCreated.Task);
            clock.Advance(OwnerConsoleLoopOptions.Default.ShutdownBound);
            await Finished(shutdown);
            Assert.Equal(0, Volatile.Read(ref disposals));
        }
        finally
        {
            stop.Cancel();
            release.Set();
            await Finished(refresh);
            if (shutdown is not null) await Finished(disposed.Task);
        }
        await Finished(observed.Task);
        Assert.Same(error, observed.Task.Result);
        Assert.Equal(1, disposals);
        Assert.Empty(fixture.View.NoticeStrip.Visible);
    }

    private static OwnerConductEvent Landing(DateTimeOffset at) =>
        new(at, "loop-relaunch", "11111111", "LOOP_RELAUNCH_NOT_REQUIRED");

    private static async Task Finished(Task refresh)
    {
        try { await refresh.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken); }
        catch (TimeoutException) { Assert.Fail("Refresh completion was not signalled."); }
    }

    private sealed class Fixture : IDisposable
    {
        internal readonly OwnerConsoleTestClock Clock = new();
        internal readonly OwnerConsoleViewModelBuilder Builder;
        internal readonly OwnerConsoleFullScreenView View;
        internal readonly OwnerRefreshCoordinator Refresh;
        private readonly IApplication _app = Application.Create();
        internal Func<IReadOnlyList<OwnerConductEvent>> ReadLifecycle = () => [];
        internal int Reads;
        internal int Renders;
        internal int TimingWrites;

        internal Fixture(CancellationToken token = default)
        {
            var harness = new OwnerConsoleHarness(Clock);
            var snapshots = new OwnerConsoleRefreshGoalSnapshots(harness.State);
            Builder = new(harness.State, harness.Questions, harness.Liveness, new Epics(), Clock, snapshots: snapshots);
            var controller = OwnerConsoleControllerFactory.Create(harness.Questions, harness.Answers, new Dialogs(),
                harness.State, harness.Tail, harness.Conductor, harness.DigestReport, harness.Digest, Clock);
            View = new(_app, controller, () => Task.CompletedTask, clock: Clock);
            Refresh = new(Clock, token, Builder, null, snapshots, new(Clock), new(), null,
                (_, _) => { Interlocked.Increment(ref Reads); return ReadLifecycle(); }, () => 0,
                _ => TimingWrites++, action => action(),
                (phase, action) => { Assert.Equal("refresh-render", phase); action(); Renders++; }, () => View);
        }

        public void Dispose() { Refresh.Dispose(); View.Dispose(); _app.Dispose(); }
    }

    private sealed class Epics : IOwnerGoalEpicLookup
    {
        public Task<string> GetTitleAsync(string id, CancellationToken token) => Task.FromResult(string.Empty);
    }

    private sealed class Dialogs : IOwnerConsoleDialogs
    {
        public Task<bool> ConfirmAsync(string title, string text) => throw new InvalidOperationException("Unexpected dialog.");
        public Task<string?> PromptTextAsync(string title, string text) => throw new InvalidOperationException("Unexpected dialog.");
        public Task ShowTextAsync(string title, string text) => throw new InvalidOperationException("Unexpected dialog.");
    }
}
