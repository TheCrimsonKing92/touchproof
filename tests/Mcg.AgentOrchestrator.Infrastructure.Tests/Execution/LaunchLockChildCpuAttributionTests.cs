using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: no live provider changes, native processes, or timers.
public sealed class LaunchLockChildCpuAttributionTests
{
    [Fact]
    public void Exit_CreditsClassCapturedAtLaunchExactlyOnce()
    {
        var ledger = new LaunchLockClassCpuLedger();
        var tracker = new LaunchLockChildCpuTracker(ledger);
        var attribution = new LaunchLockAttribution();
        var key = "A";
        attribution.SetProvider(() => key);
        var token = Assert.IsType<LaunchLockChildCpuTracker.Token>(tracker.Capture(attribution));
        Assert.Empty(ledger.FormatCpuByClass(42)); // Pending CPU must not be presented as measured zero.
        key = "B";
        token.Exited(TimeSpan.FromMilliseconds(120.25));
        token.Exited(TimeSpan.FromMilliseconds(999));
        Assert.Equal("LAUNCH_LOCK_CPU_BY_CLASS pid=42 class=A child_cpu_ms=120.250 inproc_cpu_ms_apportioned=0.000",
            Assert.Single(ledger.FormatCpuByClass(42)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("(none)")]
    [InlineData("throw")]
    public void MissingContext_UsesUnattributed(string? key)
    {
        var ledger = new LaunchLockClassCpuLedger();
        var tracker = new LaunchLockChildCpuTracker(ledger);
        var attribution = new LaunchLockAttribution();
        attribution.SetProvider(() => key == "throw" ? throw new InvalidOperationException() : key);
        tracker.Capture(attribution)!.Exited(TimeSpan.FromMilliseconds(5));
        Assert.Equal("LAUNCH_LOCK_CPU_BY_CLASS pid=42 class=unattributed child_cpu_ms=5.000 inproc_cpu_ms_apportioned=0.000",
            Assert.Single(ledger.FormatCpuByClass(42)));
    }

    [Fact]
    public void UnavailableExitReading_OmitsOnlyTheAffectedClass()
    {
        var ledger = new LaunchLockClassCpuLedger();
        var tracker = new LaunchLockChildCpuTracker(ledger);
        var attribution = new LaunchLockAttribution();
        Assert.Null(tracker.Capture(attribution));
        attribution.SetProvider(() => "A");
        tracker.Capture(attribution)!.Exited(TimeSpan.FromMilliseconds(10));
        tracker.Capture(attribution)!.Exited(null);
        attribution.SetProvider(() => "B");
        tracker.Capture(attribution)!.Exited(TimeSpan.Zero);
        Assert.Equal("LAUNCH_LOCK_CPU_BY_CLASS pid=42 class=B child_cpu_ms=0.000 inproc_cpu_ms_apportioned=0.000",
            Assert.Single(ledger.FormatCpuByClass(42)));
    }
}
