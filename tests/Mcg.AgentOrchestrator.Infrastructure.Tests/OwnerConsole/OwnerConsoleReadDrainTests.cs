using Mcg.AgentOrchestrator.App.OwnerConsole;

// Parallel-safe: reads, disposal, deadlines and completion signals belong to each test.
public sealed class OwnerConsoleReadDrainTests
{
    [Fact]
    public async Task StartupBoard_CanceledWaitRetainsReadUntilLateFault()
    {
        var clock = new ManualStewardTimeProvider();
        var entered = Signal();
        var release = Signal();
        var disposed = Signal();
        var observed = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        var error = new IOException("late board failure");
        var disposals = 0;
        using var stop = new CancellationTokenSource();
        var read = Task.Run(async () => { entered.SetResult(); await release.Task; throw error; });
        var wrapper = read.WaitAsync(stop.Token);
        await Finished(entered.Task);
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wrapper);
        var shutdown = OwnerConsoleScreenOperation.DrainAsync(wrapper, () => read, () =>
        {
            Interlocked.Increment(ref disposals);
            disposed.SetResult();
            return ValueTask.CompletedTask;
        }, clock, ex => observed.TrySetResult(ex));
        try
        {
            await Finished(clock.TimerCreated.Task);
            clock.Advance(OwnerConsoleLoopOptions.Default.ShutdownBound);
            await Finished(shutdown);
            Assert.Equal(0, Volatile.Read(ref disposals)); // Only this test can release the read.
        }
        finally { release.TrySetResult(); await Finished(disposed.Task); }
        await Finished(observed.Task);
        Assert.Same(error, observed.Task.Result);
        Assert.Equal(1, disposals);
    }

    [Fact]
    public async Task Producers_RegisterLateReadBeforeDrainCapturesCompletion()
    {
        var clock = new ManualStewardTimeProvider();
        var releaseProducer = Signal();
        var releaseRead = Signal();
        var captured = Signal();
        var disposed = Signal();
        Task read = Task.CompletedTask;
        var producer = Task.Run(async () => { await releaseProducer.Task; read = releaseRead.Task; });
        var disposals = 0;
        var shutdown = OwnerConsoleScreenOperation.DrainAsync(producer, () =>
        {
            captured.SetResult();
            return read;
        }, () =>
        {
            Interlocked.Increment(ref disposals);
            disposed.SetResult();
            return ValueTask.CompletedTask;
        }, clock, ex => Assert.Fail(ex.ToString()));
        try
        {
            await Finished(clock.TimerCreated.Task);
            clock.Advance(OwnerConsoleLoopOptions.Default.ShutdownBound);
            await Finished(shutdown);
            Assert.Equal(0, Volatile.Read(ref disposals));
            releaseProducer.SetResult();
            await Finished(captured.Task);
            Assert.Equal(0, Volatile.Read(ref disposals)); // The newly registered read is still held.
        }
        finally
        {
            releaseProducer.TrySetResult();
            releaseRead.TrySetResult();
            await Finished(disposed.Task);
        }
        Assert.Equal(1, disposals);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task StartupPanes_CanceledWrappersRetainBothUnderlyingReads(bool failQuestions)
    {
        var clock = new ManualStewardTimeProvider();
        var harness = new OwnerConsoleHarness();
        var questions = new HeldQuestions();
        var activity = new HeldActivity();
        var builder = new OwnerConsoleViewModelBuilder(harness.State, questions, harness.Liveness, new Epics(), harness.Clock);
        var renders = 0;
        var startup = new OwnerConsoleStartupLoader(builder, activity, action => action());
        var board = await builder.BuildBoardAsync();
        using var stop = new CancellationTokenSource();
        var fill = startup.FillAsync(board, _ => renders++, new(harness.Clock.GetUtcNow(), null, [], 0),
            _ => Assert.Fail("Canceled startup must not publish"), stop.Token);
        var disposed = Signal();
        var observed = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        var disposals = 0;
        Task? shutdown = null;
        var error = new IOException("late pane failure");
        try
        {
            await Finished(Task.WhenAll(questions.Entered.Task, activity.Entered.Task));
            stop.Cancel();
            await Finished(fill);
            shutdown = OwnerConsoleScreenOperation.DrainAsync(fill, () => startup.Completion, () =>
            {
                Interlocked.Increment(ref disposals);
                disposed.SetResult();
                return ValueTask.CompletedTask;
            }, clock, ex => observed.TrySetResult(ex));
            await Finished(clock.TimerCreated.Task);
            clock.Advance(OwnerConsoleLoopOptions.Default.ShutdownBound);
            await Finished(shutdown);
            Assert.Equal(0, Volatile.Read(ref disposals));
            if (failQuestions) activity.Release.SetResult(new([], null));
            else questions.Release.SetResult([]);
            Assert.Equal(0, Volatile.Read(ref disposals)); // The other underlying pane is still held.
            if (failQuestions) questions.Release.SetException(error);
            else activity.Release.SetException(error);
            await Finished(disposed.Task);
            await Finished(observed.Task);
            Assert.Same(error, observed.Task.Result);
            Assert.Equal(1, disposals);
            Assert.Equal(0, renders);
        }
        finally
        {
            stop.Cancel();
            questions.Release.TrySetResult([]);
            activity.Release.TrySetResult(new([], null));
            await Finished(fill);
            if (shutdown is not null) await Finished(disposed.Task);
        }
    }

    [Fact]
    public async Task CompletedReads_SynchronousSlowDisposalCannotBlockShutdownBound()
    {
        var clock = new ManualStewardTimeProvider();
        var entered = Signal();
        var release = Signal();
        var disposed = Signal();
        var disposals = 0;
        var shutdown = OwnerConsoleScreenOperation.DrainAsync(Task.CompletedTask, () => Task.CompletedTask, () =>
        {
            entered.SetResult();
            release.Task.GetAwaiter().GetResult(); // Intentionally synchronous, cancellation-ignoring disposal.
            Interlocked.Increment(ref disposals);
            disposed.SetResult();
            return ValueTask.CompletedTask;
        }, clock, ex => Assert.Fail(ex.ToString()));
        try
        {
            await Finished(entered.Task);
            await Finished(clock.TimerCreated.Task);
            clock.Advance(OwnerConsoleLoopOptions.Default.ShutdownBound);
            await Finished(shutdown);
            Assert.Equal(0, Volatile.Read(ref disposals));
        }
        finally { release.TrySetResult(); await Finished(disposed.Task); }
        Assert.Equal(1, disposals);
    }

    [Fact]
    public async Task CompletedReads_DisposeExactlyOnceWithoutAdvancingClock()
    {
        var captures = 0;
        var disposals = 0;
        var failures = new List<Exception>();
        await Finished(OwnerConsoleScreenOperation.DrainAsync(Task.CompletedTask, () =>
        {
            captures++;
            return Task.CompletedTask;
        }, () =>
        {
            disposals++;
            return ValueTask.CompletedTask;
        }, new ManualStewardTimeProvider(), failures.Add));
        Assert.Equal(1, captures);
        Assert.Equal(1, disposals);
        Assert.Empty(failures);
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static Task Finished(Task task) => task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

    private sealed class HeldQuestions : IOwnerQuestionSource
    {
        internal readonly TaskCompletionSource Entered = Signal();
        internal readonly TaskCompletionSource<IReadOnlyList<OwnerQuestion>> Release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<IReadOnlyList<OwnerQuestion>> ListOpenAsync(CancellationToken token)
        { Entered.SetResult(); return Release.Task; }
    }

    private sealed class HeldActivity : IOwnerConsoleActivityLoader
    {
        internal readonly TaskCompletionSource Entered = Signal();
        internal readonly TaskCompletionSource<OwnerConsoleActivityLoad> Release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<OwnerConsoleActivityLoad> LoadAsync(IReadOnlyCollection<string> ids, CancellationToken token)
        { Entered.SetResult(); return Release.Task; }
    }

    private sealed class Epics : IOwnerGoalEpicLookup
    {
        public Task<string> GetTitleAsync(string id, CancellationToken token) => Task.FromResult(string.Empty);
    }
}
