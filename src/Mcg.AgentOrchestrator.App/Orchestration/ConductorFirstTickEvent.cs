using System.Diagnostics;
using System.Globalization;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed class ConductorFirstTickEvent(
    Func<DateTimeOffset?> startedAt, Func<DateTimeOffset> clock, Action<string> emit,
    Action<ConductorLoopFirstTickRecord>? record = null, Func<long?>? sweepTotal = null)
{
    private int _observed;

    internal static ConductorFirstTickEvent ForCurrentProcess(string? conductEventsLogPath = null) => new(
        () =>
        {
            try
            {
                using var process = Process.GetCurrentProcess();
                return new DateTimeOffset(process.StartTime.ToUniversalTime());
            }
            catch { return null; }
        },
        () => DateTimeOffset.UtcNow,
        line => ConductorBatchLoop.EmitProgress(line),
        conductEventsLogPath is null ? null : value => new ConductEventLogWriter(conductEventsLogPath).AppendRecord(value),
        () => StartupSweepTotal.Milliseconds);

    internal void Observe()
    {
        if (Interlocked.Exchange(ref _observed, 1) != 0)
            return;

        var start = startedAt();
        var now = start is null ? (DateTimeOffset?)null : clock();
        var elapsed = start is null ? (long?)null : Math.Max(0, (now!.Value - start.Value).Ticks / TimeSpan.TicksPerMillisecond);
        var line = start is null
            ? "STARTUP_FIRST_TICK elapsed_ms=unknown process_started=unknown"
            : $"STARTUP_FIRST_TICK elapsed_ms={elapsed!.Value.ToString(CultureInfo.InvariantCulture)} process_started={start.Value.ToString("O", CultureInfo.InvariantCulture)}";
        try { emit(line); } catch { }
        try { record?.Invoke(new(now ?? DateTimeOffset.UtcNow, elapsed, start, sweepTotal?.Invoke())); } catch { }
    }
}
