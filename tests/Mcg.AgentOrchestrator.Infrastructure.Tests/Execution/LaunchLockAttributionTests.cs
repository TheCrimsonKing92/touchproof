using System.Diagnostics;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: private attribution and counters; the assembly provider is never mutated.
public sealed class LaunchLockAttributionTests
{
    [Fact]
    public void RecordInto_AttributesKeysWithoutChangingProcessOrCaptureCounters()
    {
        var process = new LaunchLockCounters();
        var control = new LaunchLockCounters();
        var attribution = new LaunchLockAttribution();
        string? key = "A";
        attribution.SetProvider(() => key);
        using var capture = LaunchLockTelemetry.BeginCapture();
        Record(LaunchLockEntryPoint.GitCli, 11);
        Record(LaunchLockEntryPoint.Start, 13);
        key = "B";
        Record(LaunchLockEntryPoint.GitCli, 17);
        Record(LaunchLockEntryPoint.GitCli, 19);
        key = null;
        Record(LaunchLockEntryPoint.AcquireConsoleForChildSpawn, 23);
        attribution.SetProvider(() => throw new InvalidOperationException("provider failure"));
        Record(LaunchLockEntryPoint.Start, 29);

        var rows = attribution.Snapshot().OrderBy(row => row.Key).ToArray();
        Assert.Equal(new[] { "A", "B" }, rows.Select(row => row.Key));
        Assert.Equal(2, rows[0].Launches);
        Assert.Equal(24, rows[0].CreateTicksTotal);
        Assert.Equal(new long[] { 0, 0, 0, 1, 1 }, rows[0].ByEntry);
        Assert.Equal(2, rows[1].Launches);
        Assert.Equal(36, rows[1].CreateTicksTotal);
        Assert.Equal(new long[] { 0, 0, 0, 0, 2 }, rows[1].ByEntry);
        Assert.Equal(6, process.Snapshot().Launches);
        Assert.Equal(112, process.Snapshot().CreateTicksTotal);
        var expected = LaunchLockTelemetry.FormatSummary(control.Snapshot(), 42);
        Assert.Equal(expected, LaunchLockTelemetry.FormatSummary(process.Snapshot(), 42));
        Assert.Equal(expected, LaunchLockTelemetry.FormatSummary(capture.Snapshot(), 42));
        Assert.Equal(6, capture.Samples.Count);

        void Record(LaunchLockEntryPoint entry, long create)
        {
            control.Record(entry, 2, 3, create);
            LaunchLockTelemetry.RecordInto(process, attribution, entry, 2, 3, create);
        }
    }

    [Fact]
    public void NoProvider_TeardownIsByteIdenticalToOriginalSummary()
    {
        var frequency = Stopwatch.Frequency;
        var snapshot = new LaunchLockSnapshot(
            10, frequency * 4, frequency, frequency * 2, frequency * 4,
            frequency * 8, frequency * 2, frequency * 4, frequency * 6,
            frequency, [1, 2, 3, 4, 0]);
        const string expected = "LAUNCH_LOCK_SUMMARY pid=4242 launches=10 " +
            "wait_ms_total=4000.000 wait_p50_ms=1000.000 wait_p95_ms=2000.000 wait_max_ms=4000.000 " +
            "hold_ms_total=8000.000 hold_p50_ms=2000.000 hold_p95_ms=4000.000 hold_max_ms=6000.000 " +
            "create_ms_total=1000.000 by_entry=AcquireConsoleForChildSpawn:1,AcquireSuppressedChildSpawn:2," +
            "AcquireErrorModeForChildSpawn:3,Start:4,GitCli:0";
        var attribution = new LaunchLockAttribution();
        attribution.Record(LaunchLockEntryPoint.Start, frequency);
        Assert.Empty(attribution.Snapshot());
        Assert.Equal(expected, Assert.Single(LaunchLockTelemetry.FormatTeardownLines(snapshot, null, 4242)));
        Assert.Equal(expected, Assert.Single(LaunchLockTelemetry.FormatTeardownLines(snapshot, attribution, 4242)));
    }

    [Fact]
    public void FormatByClass_EmitsOnlyFiftyHeaviestInDescendingOrder()
    {
        var attribution = new LaunchLockAttribution();
        string key = "";
        attribution.SetProvider(() => key);
        for (var i = 0; i < 60; i++)
        {
            key = $"Class{i:D2}";
            for (var n = 0; n <= i; n++) attribution.Record(LaunchLockEntryPoint.GitCli, Stopwatch.Frequency);
        }
        var lines = LaunchLockTelemetry.FormatByClass(attribution.Snapshot(), 4242).ToArray();
        Assert.Equal(50, lines.Length);
        for (var i = 0; i < lines.Length; i++)
            Assert.StartsWith($"LAUNCH_LOCK_BY_CLASS pid=4242 class=Class{59 - i:D2} launches={60 - i} ", lines[i]);
        Assert.Equal("LAUNCH_LOCK_BY_CLASS pid=4242 class=Class59 launches=60 create_ms_total=60000.000 " +
            "by_entry=AcquireConsoleForChildSpawn:0,AcquireSuppressedChildSpawn:0,AcquireErrorModeForChildSpawn:0,Start:0,GitCli:60", lines[0]);
    }

    [Fact]
    public void FormatByClass_TiesUseOrdinalKeysAndWhitespaceIsSanitized()
    {
        var attribution = new LaunchLockAttribution();
        foreach (var key in new[] { "Z\r\n Class", "A", "" })
        {
            attribution.SetProvider(() => key);
            attribution.Record(LaunchLockEntryPoint.Start, -1);
        }
        var lines = LaunchLockTelemetry.FormatByClass(attribution.Snapshot(), 1).ToArray();
        Assert.Contains("class=(none) launches=1 create_ms_total=0.000 ", lines[0]);
        Assert.Contains("class=A launches=1 ", lines[1]);
        Assert.Contains("class=Z___Class launches=1 ", lines[2]);
    }

    [Fact]
    public async Task AssemblyFixture_OptInWritesSummaryThenByClassLines()
    {
        var attribution = new LaunchLockAttribution();
        attribution.SetProvider(() => "A");
        attribution.Record(LaunchLockEntryPoint.GitCli, Stopwatch.Frequency);
        attribution.SetProvider(() => "B");
        attribution.Record(LaunchLockEntryPoint.Start, 0);
        using var diagnostics = new StringWriter();
        await new AssemblyTempRootCleanupFixture(() => null, diagnostics, attribution: attribution).DisposeAsync();
        var lines = diagnostics.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, lines.Length);
        Assert.StartsWith($"LAUNCH_LOCK_SUMMARY pid={Environment.ProcessId} ", lines[0]);
        Assert.StartsWith($"LAUNCH_LOCK_BY_CLASS pid={Environment.ProcessId} class=A ", lines[1]);
        Assert.StartsWith($"LAUNCH_LOCK_BY_CLASS pid={Environment.ProcessId} class=B ", lines[2]);
        using var noAttribution = new StringWriter();
        await new AssemblyTempRootCleanupFixture(() => null, noAttribution).DisposeAsync();
        Assert.Single(noAttribution.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
    }
}
