using Mcg.AgentOrchestrator.Infrastructure;

public sealed class FocusedEvidenceExecutorWaitQueueTests
{
    private static readonly RemoteLaneExecutorEntry Entry = new("executor", 60);

    [Fact]
    public async Task FreedSlotRunsHeaviestWaiterThenOrdinalTie()
    {
        var clock = new Clock();
        var slots = 0;
        using var queue = new FocusedEvidenceExecutorWaitQueue(() => slots-- > 0 ? Entry : null,
            _ => slots++, clock, TimeSpan.FromSeconds(1));
        var light = queue.RegisterAsync("light", 400, TimeSpan.FromSeconds(400), default);
        // Receipt identities deliberately sort opposite to the actual filter priorities.
        var zulu = queue.RegisterAsync("a-lane", 600, TimeSpan.FromSeconds(600), default, "ZuluFilter");
        var alpha = queue.RegisterAsync("z-lane", 600, TimeSpan.FromSeconds(600), default, "AlphaFilter");
        Assert.False(light.IsCompleted);
        slots = 1;
        clock.Advance(TimeSpan.FromSeconds(10));
        var acquired = await Completed(alpha, "highest-priority waiter acquired");
        Assert.Same(Entry, acquired.Entry);
        Assert.Equal("acquired", acquired.Record.Outcome);
        Assert.Equal(TimeSpan.FromSeconds(10), acquired.Record.Elapsed);
        Assert.Equal("executor", acquired.Record.ExecutorId);
        Assert.False(zulu.IsCompleted);
        Assert.False(light.IsCompleted);
        slots = 1;
        queue.Signal();
        Assert.Same(Entry, (await Completed(zulu, "second waiter acquired")).Entry);
        slots = 1;
        queue.Signal();
        Assert.Same(Entry, (await Completed(light, "last waiter acquired")).Entry);
    }

    [Fact]
    public async Task DeadlineWinsEvenWhenSlotIsAvailableAtExpiry()
    {
        var clock = new Clock();
        var claims = 0;
        using var queue = new FocusedEvidenceExecutorWaitQueue(() => { claims++; return Entry; },
            _ => throw new InvalidOperationException("No claim should need release"), clock, TimeSpan.FromSeconds(1));
        var wait = queue.RegisterAsync("heavy", 400, TimeSpan.FromSeconds(400), default);
        clock.Advance(TimeSpan.FromSeconds(400));
        var expired = await Completed(wait, "wait deadline expired");
        Assert.Null(expired.Entry);
        Assert.Equal("expired-local-fallback", expired.Record.Outcome);
        Assert.Equal(TimeSpan.FromSeconds(400), expired.Record.Elapsed);
        Assert.Equal(TimeSpan.FromSeconds(400), expired.Record.Bound);
        Assert.Equal(0, claims);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ClaimThatOutlivesCancellationOrDeadlineIsReleased(bool cancel)
    {
        var clock = new Clock();
        using var cancellation = new CancellationTokenSource();
        var releases = 0;
        using var queue = new FocusedEvidenceExecutorWaitQueue(() =>
        {
            if (cancel) cancellation.Cancel();
            else clock.ElapseWithoutTimers(TimeSpan.FromSeconds(400));
            return Entry;
        }, entry => { Assert.Same(Entry, entry); releases++; }, clock, TimeSpan.FromSeconds(1));
        var wait = queue.RegisterAsync("heavy", 400, TimeSpan.FromSeconds(400), cancellation.Token);
        queue.Signal();
        var result = await Completed(wait, "late claim released");
        Assert.Null(result.Entry);
        Assert.Equal(cancel ? "cancelled" : "expired-local-fallback", result.Record.Outcome);
        Assert.Equal(1, releases);
    }

    [Fact]
    public async Task CancellationDisposalAndClaimFailureCompleteWaiters()
    {
        var clock = new Clock();
        using var cancellation = new CancellationTokenSource();
        using var queue = new FocusedEvidenceExecutorWaitQueue(() => throw new IOException("claim-failed"),
            _ => { }, clock, TimeSpan.FromSeconds(1));
        var cancelled = queue.RegisterAsync("cancel", 400, TimeSpan.FromSeconds(400), cancellation.Token);
        cancellation.Cancel();
        Assert.Equal("cancelled", (await Completed(cancelled, "waiter cancelled")).Record.Outcome);
        var failed = queue.RegisterAsync("fail", 400, TimeSpan.FromSeconds(400), default);
        queue.Signal();
        Assert.Equal("failed", (await Completed(failed, "claim failure recorded")).Record.Outcome);
        var disposed = queue.RegisterAsync("dispose", 400, TimeSpan.FromSeconds(400), default);
        queue.Dispose();
        Assert.Equal("cancelled", (await Completed(disposed, "queue disposed")).Record.Outcome);
        Assert.Throws<ObjectDisposedException>(() => { _ = queue.RegisterAsync("late", 400, TimeSpan.FromSeconds(400), default); });
    }

    private static async Task<T> Completed<T>(Task<T> task, string name)
    {
        try { return await task.WaitAsync(TimeSpan.FromSeconds(30)); }
        catch (TimeoutException) { throw new TimeoutException("Missing event: " + name); }
    }

    private sealed class Clock : TimeProvider
    {
        private readonly ManualStewardTimeProvider _timers = new();
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(_ticks);
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            _timers.CreateTimer(callback, state, dueTime, period);
        internal void ElapseWithoutTimers(TimeSpan by) => _ticks += by.Ticks;
        internal void Advance(TimeSpan by) { ElapseWithoutTimers(by); _timers.Advance(by); }
    }
}
