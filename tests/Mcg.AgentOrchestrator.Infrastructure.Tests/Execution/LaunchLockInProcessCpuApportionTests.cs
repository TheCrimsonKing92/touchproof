using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: private ledger, fake CPU readings, and directly driven clock ticks.
public sealed class LaunchLockInProcessCpuApportionTests
{
    [Fact]
    public void Intervals_SplitConcurrentClassesThenCreditTheLoneClass()
    {
        var ledger = new LaunchLockClassCpuLedger();
        var clock = new FakeClock();
        using var sampler = new LaunchLockInProcessCpuSampler(ledger, () => clock.Cpu);
        ledger.Enter("A");
        ledger.Enter("B");
        clock.Tick(sampler, 100);
        Assert.Equal(new[] { Row("A", "50.000"), Row("B", "50.000") }, ledger.FormatCpuByClass(42));
        ledger.Exit("B");
        // B overlapped this interval even though it leaves before the boundary.
        clock.Tick(sampler, 100);
        clock.Tick(sampler, 100);
        Assert.Equal(new[] { Row("A", "200.000"), Row("B", "100.000") }, ledger.FormatCpuByClass(42));
    }

    [Fact]
    public void Membership_CountsDistinctClassesAndRetainsMidIntervalOverlap()
    {
        var ledger = new LaunchLockClassCpuLedger();
        var clock = new FakeClock();
        using var sampler = new LaunchLockInProcessCpuSampler(ledger, () => clock.Cpu);
        ledger.Enter("A");
        ledger.Enter("A");
        ledger.Enter("B");
        ledger.Exit("B");
        ledger.Exit("A");
        clock.Tick(sampler, 1);
        Assert.Equal(new[] { Row("A", "0.500"), Row("B", "0.500") }, ledger.FormatCpuByClass(42));
        clock.Tick(sampler, 1);
        Assert.Equal(new[] { Row("A", "1.500"), Row("B", "0.500") }, ledger.FormatCpuByClass(42));
    }

    [Fact]
    public void Stop_FlushesFinalDeltaOnceAndCreditsEmptyIntervalsToUnattributed()
    {
        var ledger = new LaunchLockClassCpuLedger();
        var clock = new FakeClock();
        using var sampler = new LaunchLockInProcessCpuSampler(ledger, () => clock.Cpu);
        clock.Tick(sampler, 20);
        ledger.Enter("A B");
        clock.Cpu += TimeSpan.FromMilliseconds(30);
        sampler.StopAndFlush();
        clock.Tick(sampler, 100);
        sampler.StopAndFlush();
        Assert.Equal(new[] { Row("A_B", "30.000"), Row("unattributed", "20.000") }, ledger.FormatCpuByClass(42));
    }

    [Fact]
    public void FailedReading_OmitsIncompleteRowsAndDiagnosesOnce()
    {
        var ledger = new LaunchLockClassCpuLedger();
        TimeSpan? cpu = TimeSpan.Zero;
        var diagnostics = new List<string>();
        using var sampler = new LaunchLockInProcessCpuSampler(ledger, () => cpu, diagnostic: diagnostics.Add);
        ledger.Enter("A");
        cpu = TimeSpan.FromMilliseconds(10);
        sampler.Sample();
        Assert.NotEmpty(ledger.FormatCpuByClass(42));
        cpu = null;
        sampler.Sample();
        sampler.StopAndFlush();
        Assert.Empty(ledger.FormatCpuByClass(42));
        Assert.Single(diagnostics);
    }

    private static string Row(string name, string cpu) =>
        $"LAUNCH_LOCK_CPU_BY_CLASS pid=42 class={name} child_cpu_ms=0.000 inproc_cpu_ms_apportioned={cpu}";

    private sealed class FakeClock
    {
        internal TimeSpan Cpu;
        internal void Tick(LaunchLockInProcessCpuSampler sampler, double delta)
        {
            Cpu += TimeSpan.FromMilliseconds(delta);
            sampler.Sample();
        }
    }
}
