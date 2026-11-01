using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;

// Parallel-safe: isolated log directory and injected startup measurements.
public sealed class ConductorLoopFirstTickEventTests
{
    [Fact]
    public void ObserveAppendsOneStructuredEventWithTheSameStartupMeasurements()
    {
        var directory = Path.Combine(Path.GetTempPath(), "first-tick-" + Guid.NewGuid().ToString("N"));
        try
        {
            var start = new DateTimeOffset(2026, 11, 1, 17, 37, 3, TimeSpan.Zero);
            var lines = new List<string>();
            var writer = new ConductEventLogWriter(Path.Combine(directory, "conduct-events.log"));
            var firstTick = new ConductorFirstTickEvent(() => start, () => start.AddMilliseconds(37689),
                lines.Add, writer.AppendRecord, () => 8702);

            firstTick.Observe();
            firstTick.Observe();

            Assert.Contains("elapsed_ms=37689", Assert.Single(lines));
            using var json = JsonDocument.Parse(Assert.Single(File.ReadAllLines(writer.CurrentPath)));
            var record = json.RootElement;
            Assert.Equal("loop-first-tick", record.GetProperty("eventKind").GetString());
            Assert.Equal(37689, record.GetProperty("elapsed_ms").GetInt64());
            Assert.Equal(start, record.GetProperty("process_started").GetDateTimeOffset());
            Assert.Equal(start.AddMilliseconds(37689), record.GetProperty("timestamp").GetDateTimeOffset());
            Assert.Equal(8702, record.GetProperty("sweep_total_ms").GetInt64());
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public void UnknownStartAndRecordFailurePreserveTheExistingEmission()
    {
        var lines = new List<string>();
        var records = new List<ConductorLoopFirstTickRecord>();
        var firstTick = new ConductorFirstTickEvent(() => null,
            () => throw new InvalidOperationException("Unknown start must not read the clock"), lines.Add,
            record => { records.Add(record); throw new IOException("unwritable log"); });
        firstTick.Observe();
        firstTick.Observe();
        Assert.Single(lines);
        var record = Assert.Single(records);
        Assert.Null(record.ElapsedMs);
        Assert.Null(record.ProcessStarted);
        Assert.Null(record.SweepTotalMs);
    }
}
