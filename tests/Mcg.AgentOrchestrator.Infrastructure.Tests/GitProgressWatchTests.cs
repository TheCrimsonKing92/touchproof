using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: every child, history and monotonic clock belongs to its test.
public sealed class GitProgressWatchTests
{
    [Theory]
    [InlineData("cpu")]
    [InlineData("stdout")]
    [InlineData("stderr")]
    public void Wait_ContinuedProgressBeyondSixtySeconds_DoesNotStall(string signal)
    {
        var child = new FakeChild(700, (value, _) =>
        {
            if (signal == "cpu") value.Cpu += TimeSpan.FromMilliseconds(1);
            if (signal == "stdout") value.OutputBytes++;
            if (signal == "stderr") value.ErrorBytes++;
        });
        var result = Watch(child, TimeSpan.FromMilliseconds(200));
        Assert.False(result.Stalled);
        Assert.Equal(700, result.Last.Samples);
        Assert.True(child.Clock.Elapsed > TimeSpan.FromSeconds(60));
        Assert.Equal(0, child.Kills);
        Assert.Equal(TimeSpan.Zero, result.Last.EligibleIdle);
        Assert.Equal(TimeSpan.Zero, result.LongestIdle);
    }

    [Theory]
    [InlineData("cpu")]
    [InlineData("stdout")]
    [InlineData("stderr")]
    public void Wait_ProgressAfterLongestGap_PreservesMaximum(string signal)
    {
        var child = new FakeChild(5, (value, tick) =>
        {
            if (tick != 4) return;
            if (signal == "cpu") value.Cpu += TimeSpan.FromMilliseconds(1);
            if (signal == "stdout") value.OutputBytes++;
            if (signal == "stderr") value.ErrorBytes++;
        });
        var result = Watch(child, null);
        Assert.False(result.Stalled);
        Assert.Equal(5, result.Last.Samples);
        Assert.Equal(TimeSpan.FromMilliseconds(100), result.Last.EligibleIdle);
        Assert.Equal(TimeSpan.FromMilliseconds(300), result.LongestIdle);
    }

    [Fact]
    public void Wait_IdleChild_ReturnsStallEvidenceAndKillsTree()
    {
        var child = new FakeChild(10) { Cpu = TimeSpan.FromMilliseconds(7), OutputBytes = 11, ErrorBytes = 13 };
        var watched = Watch(child, TimeSpan.FromMilliseconds(200));
        var result = GitCli.StallResult("worktree add", watched, "captured output", "captured error");
        Assert.True(watched.Stalled);
        Assert.Equal(1, child.Kills);
        Assert.Equal(TimeSpan.FromMilliseconds(300), watched.LongestIdle);
        Assert.Equal(-1, result.ExitCode);
        Assert.Equal("captured output", result.Output);
        Assert.StartsWith("captured error" + Environment.NewLine, result.Error);
        foreach (var field in new[] { "git worktree add stalled", "processorTimeMs=7", "stdoutBytes=11",
                     "stderrBytes=13", "samples=3", "starvedSamples=0", "stallWindowMs=200" })
            Assert.Contains(field, result.Error);
        Assert.DoesNotContain("timed out after", result.Error);
    }

    [Theory]
    [InlineData(300, false, 10)]
    [InlineData(100, true, 0)]
    [InlineData(200, true, 0)]
    public void Wait_TickTiming_SeparatesStarvationFromStall(int gap, bool stalled, int starved)
    {
        var child = new FakeChild(10) { Gap = TimeSpan.FromMilliseconds(gap) };
        var result = Watch(child, TimeSpan.FromMilliseconds(200));
        Assert.Equal(stalled, result.Stalled);
        Assert.Equal(starved, result.Last.StarvedSamples);
        Assert.Equal(stalled ? 1 : 0, child.Kills);
    }

    [Fact]
    public void Wait_LateTick_KeepsEarlierIdleButDoesNotAddLateInterval()
    {
        var child = new FakeChild(4, (value, tick) =>
            value.Gap = TimeSpan.FromMilliseconds(tick == 2 ? 500 : 100));
        var result = Watch(child, TimeSpan.FromMilliseconds(250));
        Assert.True(result.Stalled);
        Assert.Equal(4, result.Last.Samples);
        Assert.Equal(1, result.Last.StarvedSamples);
        Assert.Equal(TimeSpan.FromMilliseconds(300), result.Last.EligibleIdle);
        Assert.Equal(TimeSpan.FromMilliseconds(300), result.LongestIdle);
    }

    [Fact]
    public void Wait_ProgressOnLateTick_ResetsEarlierIdleAndCountsStarvation()
    {
        var child = new FakeChild(4, (value, tick) =>
        {
            value.Gap = TimeSpan.FromMilliseconds(tick == 2 ? 500 : 100);
            if (tick == 2) value.Cpu += TimeSpan.FromMilliseconds(1);
        });
        var result = Watch(child, TimeSpan.FromMilliseconds(250));
        Assert.False(result.Stalled);
        Assert.Equal(1, result.Last.StarvedSamples);
        Assert.Equal(TimeSpan.FromMilliseconds(200), result.Last.EligibleIdle);
        Assert.Equal(TimeSpan.FromMilliseconds(200), result.LongestIdle);
    }

    [Fact]
    public void Wait_UnknownCpu_ExcludesIntervalsRatherThanInferringIdle()
    {
        var child = new FakeChild(10, (value, tick) =>
            value.Cpu = tick % 2 == 0 ? TimeSpan.Zero : null);
        var result = Watch(child, TimeSpan.FromMilliseconds(1));
        Assert.False(result.Stalled);
        Assert.Equal(TimeSpan.Zero, result.Last.EligibleIdle);
        Assert.Equal(TimeSpan.Zero, result.LongestIdle);
        Assert.Equal(0, child.Kills);
    }

    [Fact]
    public void Wait_ExitAtStallDecision_PreservesNaturalExit()
    {
        var child = new FakeChild(10) { ExitedAtDecision = true };
        var result = Watch(child, TimeSpan.Zero);
        Assert.False(result.Stalled);
        Assert.Equal(0, child.Kills);
    }

    internal static GitProgressWatch.Result Watch(FakeChild child, TimeSpan? window,
        Action<string>? output = null, Action<GitProgressWatch.Observation>? sampled = null) =>
        GitCli.WaitForProgress(child, "worktree add", window, child.Clock, output ?? (_ => { }), sampled);

    internal sealed class ManualClock : TimeProvider
    {
        private long ticks;
        internal TimeSpan Elapsed => TimeSpan.FromTicks(ticks);
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => ticks;
        internal void Advance(TimeSpan value) => ticks += value.Ticks;
    }

    internal sealed class FakeChild(int steps, Action<FakeChild, int>? beforeTick = null) : GitProgressWatch.IChild
    {
        internal ManualClock Clock { get; } = new();
        internal TimeSpan Gap = GitProgressWatch.SamplingInterval;
        internal TimeSpan? Cpu = TimeSpan.Zero;
        internal long OutputBytes, ErrorBytes;
        internal int Kills, Tick;
        internal bool ExitedAtDecision;
        public bool HasExited => ExitedAtDecision || Tick > steps;
        public TimeSpan? ProcessorTime => Cpu;
        public long StdoutBytes => OutputBytes;
        public long StderrBytes => ErrorBytes;
        public bool WaitForExit(TimeSpan interval)
        {
            Assert.Equal(GitProgressWatch.SamplingInterval, interval);
            if (++Tick > steps) return true;
            beforeTick?.Invoke(this, Tick);
            Clock.Advance(Gap);
            return false;
        }
        public void KillTree() => Kills++;
    }
}
