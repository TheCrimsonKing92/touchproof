public sealed class ChildProgressWatchTests
{
    // Parallel-safe: samples and elapsed time belong to each test instance.
    [Theory]
    [InlineData("cpu")]
    [InlineData("stdout")]
    [InlineData("stderr")]
    public void ChangingSignalKeepsChildHealthyBeyondAnyTotalBound(string signal)
    {
        var now = TimeSpan.Zero;
        var sample = new ChildProgressWatch.Sample(true, TimeSpan.Zero, 0, 0);
        var watch = new ChildProgressWatch(() => sample, TimeSpan.FromSeconds(30), () => now);
        Assert.Null(watch.Observe());
        for (var step = 1; step <= 1000; step++)
        {
            now += TimeSpan.FromSeconds(29);
            sample = signal switch
            {
                "cpu" => sample with { ProcessorTime = TimeSpan.FromSeconds(step) },
                "stdout" => sample with { StdoutBytes = step },
                _ => sample with { StderrBytes = step }
            };
            Assert.Null(watch.Observe());
        }
    }

    [Fact]
    public void FrozenLiveChildStallsAtFullWindowWithObservedEvidence()
    {
        var now = TimeSpan.Zero;
        var sample = new ChildProgressWatch.Sample(true, TimeSpan.FromSeconds(2), 12, 34);
        var watch = new ChildProgressWatch(() => sample, TimeSpan.FromSeconds(30), () => now);
        Assert.Null(watch.Observe());
        now = TimeSpan.FromSeconds(29);
        sample = sample with { StdoutBytes = 56 };
        Assert.Null(watch.Observe());
        now += TimeSpan.FromSeconds(30) - TimeSpan.FromTicks(1);
        Assert.Null(watch.Observe());
        now += TimeSpan.FromTicks(1);
        var stall = Assert.IsType<ChildProgressWatch.Stall>(watch.Observe());
        Assert.Equal(sample, stall.LastObserved);
        Assert.Equal(TimeSpan.FromSeconds(30), stall.SinceLastProgress);
        Assert.Contains("processor time=2.000 s", stall.ToString());
        Assert.Contains("stdout=56 bytes; stderr=34 bytes", stall.ToString());
        Assert.Contains("time since last progress=30.000 s", stall.ToString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExitedOrUnmeasurableChildDoesNotEstablishIdleTime(bool alive)
    {
        var now = TimeSpan.Zero;
        var sample = new ChildProgressWatch.Sample(alive, null, 0, 0);
        var watch = new ChildProgressWatch(() => sample, TimeSpan.FromSeconds(30), () => now);
        Assert.Null(watch.Observe());
        now = TimeSpan.FromDays(1);
        Assert.Null(watch.Observe());
        sample = sample with { Alive = true, ProcessorTime = TimeSpan.Zero };
        Assert.Null(watch.Observe());
        now += TimeSpan.FromSeconds(30);
        Assert.NotNull(watch.Observe());
    }
}
