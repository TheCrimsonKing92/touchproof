namespace Mcg.AgentOrchestrator.Infrastructure;

// One timer per host, with a directly driven Sample seam for deterministic tests.
internal sealed class LaunchLockInProcessCpuSampler : IDisposable
{
    private readonly object gate = new();
    private readonly LaunchLockClassCpuLedger ledger;
    private readonly Func<TimeSpan?> reader;
    private readonly Action<string>? diagnostic;
    private readonly Timer? timer;
    private TimeSpan? previous;
    private bool stopped;
    private bool failed;

    internal LaunchLockInProcessCpuSampler(LaunchLockClassCpuLedger ledger, Func<TimeSpan?> reader,
        bool startTimer = false, Action<string>? diagnostic = null)
    {
        this.ledger = ledger;
        this.reader = reader;
        this.diagnostic = diagnostic;
        previous = Read();
        if (startTimer) timer = new Timer(static state => ((LaunchLockInProcessCpuSampler)state!).Sample(),
            this, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    internal static LaunchLockInProcessCpuSampler StartLive() => new(LaunchLockClassCpuLedger.Live,
        static () => Environment.CpuUsage.TotalTime, startTimer: true,
        diagnostic: static message => Console.Error.WriteLine(message));

    internal void Sample()
    {
        lock (gate)
        {
            if (!stopped) SampleCore();
        }
    }

    private void SampleCore()
    {
        var current = Read();
        if (current is { } now && previous is { } before)
        {
            if (now < before) Fail();
            else ledger.Apportion((now - before).TotalMilliseconds);
        }
        previous = current;
    }

    private TimeSpan? Read()
    {
        try
        {
            var value = reader();
            if (value is { } cpu && cpu >= TimeSpan.Zero) return cpu;
        }
        catch { /* Telemetry must not change test outcomes. */ }
        Fail();
        return null;
    }

    private void Fail()
    {
        ledger.MarkInProcessIncomplete();
        if (failed) return;
        failed = true;
        try { diagnostic?.Invoke("launch-lock-cpu measurement_failed component=inproc_cpu_ms_apportioned"); }
        catch { /* Preserve the test-host lifecycle even if the diagnostic writer failed. */ }
    }

    internal void StopAndFlush()
    {
        lock (gate)
        {
            if (stopped) return;
            timer?.Dispose();
            SampleCore();
            stopped = true;
        }
    }

    public void Dispose() => StopAndFlush();
}
