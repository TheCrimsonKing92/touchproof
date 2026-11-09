using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: unique ledger key; the installed assembly attribution provider is never changed.
public sealed class AssemblyTempRootCleanupFixtureCpuTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Dispose_ReadersCompleteBeforeCpuReceiptAndRootRelease(bool disposeThrows)
    {
        Assert.True(LaunchLockTelemetry.Attribution.IsActive);
        var key = $"{nameof(AssemblyTempRootCleanupFixtureCpuTests)}_{Guid.NewGuid():N}";
        var ledger = LaunchLockClassCpuLedger.Live;
        ledger.BeginChild(key);
        var child = new LaunchLockChildCpuTracker.Token(ledger, key);
        using var diagnostics = new StringWriter();
        var readerDisposed = false;
        var rootReleased = false;
        var receiptPrefix = $"LAUNCH_LOCK_CPU_BY_CLASS pid={Environment.ProcessId} class={key} " +
                            "child_cpu_ms=12.500 inproc_cpu_ms_apportioned=";
        using var reader = new CompletingReader(() =>
        {
            readerDisposed = true;
            child.Exited(TimeSpan.FromMilliseconds(12.5));
            if (disposeThrows) throw new InvalidOperationException("reader disposal failed");
        });
        var fixture = new AssemblyTempRootCleanupFixture(() =>
        {
            Assert.True(readerDisposed);
            Assert.Contains(receiptPrefix, diagnostics.ToString());
            rootReleased = true;
            return null;
        }, diagnostics, sharedReaders: reader, attribution: LaunchLockTelemetry.Attribution);

        try
        {
            await fixture.DisposeAsync();

            Assert.True(rootReleased);
            Assert.Single(diagnostics.ToString().Split(Environment.NewLine)
                .Where(line => line.StartsWith(receiptPrefix, StringComparison.Ordinal)));
            if (disposeThrows)
                Assert.Contains("assembly-temp-cleanup-reader-dispose-failed exception=InvalidOperationException",
                    diagnostics.ToString());
        }
        finally
        {
            child.Exited(TimeSpan.FromMilliseconds(12.5));
        }
    }

    private sealed class CompletingReader(Action complete) : IDisposable
    {
        private Action? onDispose = complete;
        public void Dispose() => Interlocked.Exchange(ref onDispose, null)?.Invoke();
    }
}
