using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: injected readers or a unique key in the live ledger.
public sealed class LaunchLockClassMemoryLedgerTests
{
    [Fact]
    public void FormatMemoryByClass_OverlappingTestsUseFirstStartAndLastEnd()
    {
        var heap = new Queue<long>([100, 200, 300, 400]);
        var privateBytes = new Queue<long>([1000, 2000, 3000, 4000]);
        var ledger = new LaunchLockClassMemoryLedger(() => heap.Dequeue(), () => privateBytes.Dequeue());

        ledger.Enter("A");
        ledger.Enter("B");
        ledger.Enter("A");
        ledger.Enter("B");
        ledger.Exit("A");
        ledger.Exit("B");
        Assert.Empty(ledger.FormatMemoryByClass(42));
        ledger.Exit("A");
        ledger.Exit("B");

        Assert.Equal(new[]
        {
            "LAUNCH_LOCK_MEMORY_BY_CLASS pid=42 class=A heap_start_bytes=100 heap_end_bytes=300 private_start_bytes=1000 private_end_bytes=3000",
            "LAUNCH_LOCK_MEMORY_BY_CLASS pid=42 class=B heap_start_bytes=200 heap_end_bytes=400 private_start_bytes=2000 private_end_bytes=4000"
        }, ledger.FormatMemoryByClass(42));
        Assert.Empty(heap);
        Assert.Empty(privateBytes);
    }

    [Fact]
    public void EnterAndExit_ReadOnlyFirstStartAndEachReturnToZero()
    {
        var heapReads = 0;
        var privateReads = 0;
        var ledger = new LaunchLockClassMemoryLedger(() => 173 + ++heapReads, () => 29 + ++privateReads);

        ledger.Exit("A");
        Assert.Equal((0, 0), (heapReads, privateReads));
        ledger.Enter("A");
        Assert.Equal((1, 1), (heapReads, privateReads));
        ledger.Enter("A");
        ledger.Exit("A");
        Assert.Empty(ledger.FormatMemoryByClass(42));
        Assert.Equal((1, 1), (heapReads, privateReads));
        ledger.Exit("A");
        Assert.Equal((2, 2), (heapReads, privateReads));

        ledger.Enter("A");
        Assert.Equal("LAUNCH_LOCK_MEMORY_BY_CLASS pid=42 class=A heap_start_bytes=174 heap_end_bytes=175 private_start_bytes=30 private_end_bytes=31",
            Assert.Single(ledger.FormatMemoryByClass(42)));
        Assert.Equal((2, 2), (heapReads, privateReads));
        ledger.Exit("A");
        ledger.Exit("A");
        Assert.Equal("LAUNCH_LOCK_MEMORY_BY_CLASS pid=42 class=A heap_start_bytes=174 heap_end_bytes=176 private_start_bytes=30 private_end_bytes=32",
            Assert.Single(ledger.FormatMemoryByClass(42)));
        Assert.Equal((3, 3), (heapReads, privateReads));
    }

    [Fact]
    public void EnterAndExit_UnavailableFirstStartIsNeverRetried()
    {
        var heapReads = 0;
        var privateReads = 0;
        var ledger = new LaunchLockClassMemoryLedger(() => ++heapReads == 1 ? null : 100, () => ++privateReads);

        ledger.Enter("A");
        ledger.Exit("A");
        ledger.Enter("A");
        ledger.Exit("A");

        Assert.Empty(ledger.FormatMemoryByClass(42));
        Assert.Equal((1, 1), (heapReads, privateReads));
    }

    [Fact]
    public void Exit_ThrowingReaderPreservesLastCompletePair()
    {
        var heap = 100L;
        var shouldThrow = false;
        var ledger = new LaunchLockClassMemoryLedger(() => heap, () =>
            shouldThrow ? throw new InvalidOperationException("sample unavailable") : 10 * heap);

        ledger.Enter("A");
        heap = 200;
        ledger.Exit("A");
        ledger.Enter("A");
        heap = 300;
        shouldThrow = true;
        ledger.Exit("A");

        Assert.Equal("LAUNCH_LOCK_MEMORY_BY_CLASS pid=42 class=A heap_start_bytes=100 heap_end_bytes=200 private_start_bytes=1000 private_end_bytes=2000",
            Assert.Single(ledger.FormatMemoryByClass(42)));
    }

    [Fact]
    public void FormatTeardownLines_EmitsMemoryForLiveAttribution()
    {
        var liveAttribution = LaunchLockTelemetry.Attribution;
        Assert.True(liveAttribution.IsActive);
        var key = $"{nameof(LaunchLockClassMemoryLedgerTests)}_{Guid.NewGuid():N}";
        LaunchLockClassMemoryLedger.Live.Enter(key);
        LaunchLockClassMemoryLedger.Live.Exit(key);
        var prefix = $"LAUNCH_LOCK_MEMORY_BY_CLASS pid=42 class={key} ";
        var snapshot = LaunchLockTelemetry.Process.Snapshot();

        Assert.Single(LaunchLockTelemetry.FormatTeardownLines(snapshot, liveAttribution, 42),
            line => line.StartsWith(prefix, StringComparison.Ordinal));
        Assert.DoesNotContain(LaunchLockTelemetry.FormatTeardownLines(snapshot, new LaunchLockAttribution(), 42),
            line => line.StartsWith("LAUNCH_LOCK_MEMORY_BY_CLASS ", StringComparison.Ordinal));
    }
}
