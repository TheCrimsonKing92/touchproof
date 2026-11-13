// Parallel-safe: every clock, sample, task and pacing callback is owned by its test.
public sealed class InProcessProgressWatchTests
{
    [Theory]
    [InlineData("cpu")]
    [InlineData("work")]
    public void ChangedMetricResetsWindowAndFrozenSamplesReportEvidence(string metric)
    {
        var now = TimeSpan.Zero;
        var window = TestHangGuard.Bound;
        var sample = new InProcessProgressWatch.Sample(TimeSpan.FromSeconds(2), 12);
        var watch = new InProcessProgressWatch(() => sample, window, () => now);
        Assert.Null(watch.Observe());
        now = window - TimeSpan.FromTicks(1);
        sample = metric == "cpu"
            ? sample with { ProcessorTime = TimeSpan.FromSeconds(3) }
            : sample with { CompletedWorkItems = 13 };
        Assert.Null(watch.Observe());
        now += window - TimeSpan.FromTicks(1);
        Assert.Null(watch.Observe());
        Assert.Equal(TimeSpan.FromTicks(1), watch.NextSlice);
        now += TimeSpan.FromTicks(1);
        var stall = Assert.IsType<InProcessProgressWatch.Stall>(watch.Observe());
        Assert.Equal(window, stall.Window);
        Assert.Equal(window, stall.SinceLastProgress);
        Assert.Equal(sample, stall.LastObserved);
        AssertEvidence(stall.ToString(), sample, window);
    }

    [Theory]
    [InlineData("cpu")]
    [InlineData("work")]
    public void ProgressingControlOutlastsWindowWhileFrozenControlStalls(string metric)
    {
        var now = TimeSpan.Zero;
        var window = TestHangGuard.Bound;
        var sample = new InProcessProgressWatch.Sample(TimeSpan.FromSeconds(2), 12);
        var frozen = sample;
        var stalled = new InProcessProgressWatch(() => frozen, window, () => now);
        var progressing = new InProcessProgressWatch(() => sample, window, () => now);
        Assert.Null(stalled.Observe());
        Assert.Null(progressing.Observe());
        for (var index = 1; index <= 10; index++)
        {
            now += window;
            sample = metric == "cpu"
                ? sample with { ProcessorTime = sample.ProcessorTime + TimeSpan.FromTicks(1) }
                : sample with { CompletedWorkItems = sample.CompletedWorkItems + 1 };
            Assert.Null(progressing.Observe());
            var stall = Assert.IsType<InProcessProgressWatch.Stall>(stalled.Observe());
            AssertEvidence(stall.ToString(), frozen, now);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void NonPositiveWindowFailsLoudly(int ticks)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new InProcessProgressWatch(
            () => new(TimeSpan.Zero, 0), TimeSpan.FromTicks(ticks), () => TimeSpan.Zero));
    }

    [Theory]
    [InlineData("plain")]
    [InlineData("generic")]
    [InlineData("within")]
    public async Task FrozenHelperFailsOnlyAtWindowWithObservedEvidence(string helper)
    {
        var now = TimeSpan.Zero;
        var source = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sample = new InProcessProgressWatch.Sample(TimeSpan.FromSeconds(2), 12);
        var wait = Start(helper, source.Task, () => now, () => sample, step =>
        {
            now = TestHangGuard.Bound - TimeSpan.FromTicks(1);
            Assert.False(step());
            now += TimeSpan.FromTicks(1);
            Assert.True(step());
        });
        Exception error = helper == "within"
            ? await Assert.ThrowsAsync<TestHangGuardExpiredException>(() => wait)
            : await Assert.ThrowsAsync<TimeoutException>(() => wait);
        Assert.Contains("owned event", error.Message);
        AssertEvidence(error.Message, sample, TestHangGuard.Bound);
        Assert.False(source.Task.IsCompleted);
    }

    [Theory]
    [InlineData("plain", "cpu")]
    [InlineData("plain", "work")]
    [InlineData("generic", "cpu")]
    [InlineData("generic", "work")]
    [InlineData("within", "cpu")]
    [InlineData("within", "work")]
    public async Task ProgressingHelperOutlastsTenWindowsThenCompletes(string helper, string metric)
    {
        var now = TimeSpan.Zero;
        var source = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sample = new InProcessProgressWatch.Sample(TimeSpan.FromSeconds(2), 12);
        var wait = Start(helper, source.Task, () => now, () => sample, step =>
        {
            for (var index = 1; index <= 10; index++)
            {
                now += TestHangGuard.Bound;
                sample = metric == "cpu"
                    ? sample with { ProcessorTime = sample.ProcessorTime + TimeSpan.FromTicks(1) }
                    : sample with { CompletedWorkItems = sample.CompletedWorkItems + 1 };
                Assert.False(step());
            }
            source.SetResult(42);
            Assert.True(step());
        });
        await wait;
        Assert.Equal(TestHangGuard.Bound * 10, now);
        if (helper == "generic") Assert.Equal(42, await (Task<int>)wait);
    }

    [Theory]
    [InlineData("plain")]
    [InlineData("generic")]
    [InlineData("within")]
    public async Task CompletedTaskSkipsClockSamplerAndPacing(string helper)
    {
        await Start(helper, Task.FromResult(42),
            () => throw new InvalidOperationException("clock must not run"),
            () => throw new InvalidOperationException("sampler must not run"),
            _ => throw new InvalidOperationException("pacer must not run"));
        // Also pin the original signatures without any injected seams.
        await TestHangGuard.WaitAsync((Task)Task.CompletedTask, "completed event");
        Assert.Equal(42, await TestHangGuard.WaitAsync(Task.FromResult(42), "completed event"));
        await TestHangGuard.CompletesWithinAsync(Task.CompletedTask, TestHangGuard.Bound, "completed event");
    }

    [Theory]
    [InlineData("plain")]
    [InlineData("generic")]
    [InlineData("within")]
    public async Task CompletionDuringStallSampleWins(string helper)
    {
        var now = TimeSpan.Zero;
        var samples = 0;
        var source = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        await Start(helper, source.Task, () => now, () =>
        {
            if (++samples == 2) source.SetResult(42);
            return new(TimeSpan.FromSeconds(2), 12);
        }, step =>
        {
            now = TestHangGuard.Bound;
            Assert.True(step());
            Assert.True(step());
        });
        Assert.Equal(2, samples);
        Assert.Equal(42, await source.Task);
    }

    [Theory]
    [InlineData("plain", false)]
    [InlineData("generic", false)]
    [InlineData("within", false)]
    [InlineData("plain", true)]
    [InlineData("generic", true)]
    [InlineData("within", true)]
    public async Task TerminalFaultAndCancellationKeepTheirBehavior(string helper, bool cancel)
    {
        using var cancellation = new CancellationTokenSource();
        var source = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var original = new TimeoutException("awaited task's own fault");
        var wait = Start(helper, source.Task, () => TimeSpan.Zero, () => new(TimeSpan.Zero, 0), step =>
        {
            if (cancel)
            {
                cancellation.Cancel();
                source.SetCanceled(cancellation.Token);
            }
            else source.SetException(original);
            Assert.True(step());
        });
        if (helper == "within") await wait;
        else if (cancel)
        {
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait);
            Assert.Equal(cancellation.Token, error.CancellationToken);
        }
        else Assert.Same(original, await Assert.ThrowsAsync<TimeoutException>(() => wait));
        _ = source.Task.Exception;
    }

    [Theory]
    [InlineData("plain", "clock")]
    [InlineData("plain", "sampler")]
    [InlineData("generic", "clock")]
    [InlineData("generic", "sampler")]
    [InlineData("within", "clock")]
    [InlineData("within", "sampler")]
    public async Task InfrastructureFailuresPropagateWithoutTimeoutWrapping(string helper, string seam)
    {
        var now = TimeSpan.Zero;
        var original = new InvalidOperationException("observation failed");
        var source = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var wait = Start(helper, source.Task,
            () => seam == "clock" && now > TimeSpan.Zero ? throw original : now,
            () => seam == "sampler" && now > TimeSpan.Zero ? throw original : new(TimeSpan.Zero, 0),
            step =>
            {
                now = TestHangGuard.Bound;
                Assert.True(step());
            });
        Assert.Same(original, await Assert.ThrowsAsync<InvalidOperationException>(() => wait));
        Assert.False(source.Task.IsCompleted);
    }

    private static Task Start(string helper, Task<int> task, Func<TimeSpan> now,
        Func<InProcessProgressWatch.Sample> sample, Action<Func<bool>> pace) => helper switch
    {
        "plain" => TestHangGuard.WaitAsync((Task)task, "owned event", now, sample, pace),
        "generic" => TestHangGuard.WaitAsync(task, "owned event", now, sample, pace),
        "within" => TestHangGuard.CompletesWithinAsync(task, TestHangGuard.Bound, "owned event", now, sample, pace),
        _ => throw new ArgumentOutOfRangeException(nameof(helper))
    };

    private static void AssertEvidence(string message, InProcessProgressWatch.Sample sample, TimeSpan since)
    {
        Assert.Contains(FormattableString.Invariant($"processor time={sample.ProcessorTime.TotalSeconds:0.000} s"), message);
        Assert.Contains(FormattableString.Invariant($"completed work items={sample.CompletedWorkItems}"), message);
        Assert.Contains(FormattableString.Invariant($"time since last progress={since.TotalSeconds:0.000} s"), message);
    }
}
