using System.Globalization;
using Mcg.AgentOrchestrator.Infrastructure;

// Each scenario owns a temporary git repository and its executor occupancy root.
[Collection("IsolatedProcessSpawningRemainderC")]
public sealed class RemoteFirstFocusedEvidenceExecutorWaitTests : RemoteFirstFocusedEvidenceFixture
{
    [Fact]
    public async Task HeavyRun_SlotFreedAfterTenSeconds_UsesRemoteAndRecordsWait()
    {
        using var scenario = CreateScenario();
        using var occupied = Occupy(scenario);
        var fake = scenario.CompletedExecutor();
        var clock = new WaitClock(scenario.Clock);
        using var cancellation = new CancellationTokenSource();
        var started = clock.GetUtcNow();
        var pending = Run(scenario, clock, 600, cancellation.Token);
        try
        {
            await Event(clock.WaitRegistered.Task, "heavy wait registered after unsuccessful claim");
            Assert.False(pending.IsCompleted);
            Assert.Empty(fake.Requests);
            Assert.Equal(0, scenario.LocalCalls);

            clock.Advance(TimeSpan.FromSeconds(10), occupied.Dispose);
            await Event(pending, "freed executor returned remote verdict");
            var result = Assert.Single((await pending).Results);
            Assert.True(result.Passed);
            Assert.Equal("remote-executor=fixture-executor", result.ResultSummary);
            Assert.Equal(3, result.ExecutedTestCount);
            Assert.Equal(0, scenario.LocalCalls);
            var request = Assert.Single(fake.Requests);
            Assert.Equal("fixture-executor", request.ExecutorId);
            Assert.Equal("FullyQualifiedName~AlphaTests", request.Filter);
            AssertWait(scenario, started, 10, "acquired", "fixture-executor");
            Assert.Contains(scenario.Progress, line => line.Contains("lane=" + request.Lane, StringComparison.Ordinal));
            Assert.Contains(scenario.Progress, line => line.Contains("predicted_seconds=600 heavy=true", StringComparison.Ordinal));
            using var released = Occupy(scenario);
        }
        finally { await CancelAndDrain(cancellation, pending); }
    }

    [Theory]
    [InlineData(10)]
    [InlineData(300)]
    public async Task LightRun_NoIdleSlot_FallsBackWithoutAdvancingClock(double seconds)
    {
        using var scenario = CreateScenario();
        using var occupied = Occupy(scenario);
        var fake = scenario.CompletedExecutor();
        var clock = new WaitClock(scenario.Clock);
        using var cancellation = new CancellationTokenSource();
        var started = clock.GetUtcNow();
        var pending = Run(scenario, clock, seconds, cancellation.Token);
        try
        {
            await Event(pending, "light fallback completed without simulated time passing");
            Assert.Equal("local-passed", Assert.Single((await pending).Results).OutputTail);
            Assert.Equal(started, clock.GetUtcNow());
            Assert.Equal(1, scenario.LocalCalls);
            Assert.Empty(fake.Requests);
            Assert.False(clock.WaitRegistered.Task.IsCompleted);
            Assert.DoesNotContain(scenario.Progress, line => line.StartsWith("REMOTE_FIRST_FOCUSED_EVIDENCE_WAIT ", StringComparison.Ordinal));
            var row = Assert.Single(scenario.Health());
            Assert.Equal(RemoteLaneOutcomeCode.ShadowSkippedNoExecutor, row.Outcome);
            Assert.Contains("no-idle-executor", row.Reason);
        }
        finally { await CancelAndDrain(cancellation, pending); }
    }

    [Fact]
    public async Task HeavyRun_WaitBoundExpires_RecordsWaitAndFallsBackLocally()
    {
        using var scenario = CreateScenario();
        using var occupied = Occupy(scenario);
        var fake = scenario.CompletedExecutor();
        var clock = new WaitClock(scenario.Clock);
        using var cancellation = new CancellationTokenSource();
        var started = clock.GetUtcNow();
        var pending = Run(scenario, clock, 600, cancellation.Token);
        try
        {
            await Event(clock.WaitRegistered.Task, "heavy wait registered");
            Assert.False(pending.IsCompleted);
            Assert.Equal(0, scenario.LocalCalls);
            clock.Advance(TimeSpan.FromSeconds(600));
            await Event(pending, "expired heavy wait returned local verdict");
            Assert.Equal("local-passed", Assert.Single((await pending).Results).OutputTail);
            Assert.Equal(1, scenario.LocalCalls);
            Assert.Empty(fake.Requests);
            AssertWait(scenario, started, 600, "expired-local-fallback", "none");
            var row = Assert.Single(scenario.Health());
            Assert.Equal(RemoteLaneOutcomeCode.ShadowSkippedNoExecutor, row.Outcome);
            Assert.Contains("wait-expired waited_ms=600000 bound_ms=600000 initial=no-idle-executor", row.Reason);
        }
        finally { await CancelAndDrain(cancellation, pending); }
    }

    private static IDisposable Occupy(Scenario scenario)
    {
        var claim = RemoteExecutorOccupancy.TryClaimExclusive(
            AcceptancePartitionVerdictCache.ResolveHostStateRoot(scenario.Root), "fixture-executor");
        Assert.NotNull(claim);
        return claim;
    }

    private static async Task<RemoteFirstFocusedEvidence.Outcome> Run(
        Scenario scenario, WaitClock clock, double seconds, CancellationToken token)
    {
        scenario.Overrides.RemoteLaneTimeProviderForTests = clock;
        scenario.Overrides.RemoteLanePollInterval = TimeSpan.FromSeconds(1);
        // The shared fixture supplies the normal local-run counter. A plan carries the
        // fake read-only snapshot through the same path as the production resolver.
        var checks = new FocusedEvidenceCheckPlan([FocusedCheck()], new History(seconds));
        return await scenario.DirectAsync(checks, token: token);
    }

    private static void AssertWait(Scenario scenario, DateTimeOffset started, int elapsed,
        string outcome, string executor)
    {
        var line = Assert.Single(scenario.Progress.Where(line =>
            line.StartsWith("REMOTE_FIRST_FOCUSED_EVIDENCE_WAIT ", StringComparison.Ordinal)));
        var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1)
            .Select(field => field.Split('=', 2)).ToDictionary(field => field[0], field => field[1]);
        Assert.False(string.IsNullOrWhiteSpace(fields["lane"]));
        Assert.Equal(started, DateTimeOffset.Parse(fields["started"], CultureInfo.InvariantCulture));
        Assert.Equal(started.AddSeconds(elapsed), DateTimeOffset.Parse(fields["ended"], CultureInfo.InvariantCulture));
        Assert.Equal(elapsed * 1000, double.Parse(fields["elapsed_ms"], CultureInfo.InvariantCulture));
        Assert.Equal(600000, double.Parse(fields["bound_ms"], CultureInfo.InvariantCulture));
        Assert.Equal(outcome, fields["outcome"]);
        Assert.Equal(executor, fields["executor"]);
    }

    private static async Task CancelAndDrain(CancellationTokenSource cancellation, Task pending)
    {
        await cancellation.CancelAsync();
        try { await Event(pending, "attempt drained before scenario disposal"); }
        catch (OperationCanceledException) { }
    }

    private sealed class History(double seconds) : IClassDurationHistory
    {
        public double? PredictSeconds(string key) => key == "Infrastructure.Tests:AlphaTests" ? seconds : null;
    }

    private sealed class WaitClock(Clock wallClock) : TimeProvider
    {
        private readonly ManualStewardTimeProvider _timers = new();
        private long _ticks;
        internal TaskCompletionSource WaitRegistered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref _ticks);
        public override DateTimeOffset GetUtcNow() => wallClock.GetUtcNow();
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = _timers.CreateTimer(callback, state, dueTime, period);
            if (dueTime == TimeSpan.FromSeconds(600) && period == Timeout.InfiniteTimeSpan)
                WaitRegistered.TrySetResult();
            return timer;
        }
        internal void Advance(TimeSpan by, Action? beforeTimers = null)
        {
            wallClock.Advance(by);
            Interlocked.Add(ref _ticks, by.Ticks);
            beforeTimers?.Invoke();
            _timers.Advance(by);
        }
    }
}
