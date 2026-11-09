using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed class LaunchLockChildCpuTracker(LaunchLockClassCpuLedger ledger)
{
    internal static LaunchLockChildCpuTracker Live { get; } = new(LaunchLockClassCpuLedger.Live);
    private readonly object gate = new();
    private readonly HashSet<LaunchLockChildCpuObservation> pending = [];

    internal Token? Capture(LaunchLockAttribution attribution)
    {
        if (!attribution.IsActive) return null;
        var key = LaunchLockClassCpuLedger.Normalize(attribution.CaptureCpuClass());
        ledger.BeginChild(key);
        return new Token(ledger, key);
    }

    internal static void Track(SafeHandle handle) => Live.Observe(handle);

    internal static void Track(IntPtr handle)
    {
        if (!LaunchLockTelemetry.Attribution.IsActive) return;
        using var borrowed = new SafeProcessHandle(handle, ownsHandle: false);
        Track(borrowed);
    }

    internal static T TrackCreated<T>(T result)
    {
        if (result is Process process) Track(process);
        return result;
    }

    // Legacy spawn scopes have no process handle. Suppress that class's incomplete CPU row.
    internal static void MarkUnavailable() => Live.Capture(LaunchLockTelemetry.Attribution)?.Exited(null);

    internal static bool TrackStarted(bool started, Process process)
    {
        if (started) Track(process);
        return started;
    }

    internal static void Track(Process process)
    {
        if (!LaunchLockTelemetry.Attribution.IsActive) return;
        try { Track(process.SafeHandle); }
        catch { Live.Capture(LaunchLockTelemetry.Attribution)?.Exited(null); ReportFailure(); }
    }

    private void Observe(SafeHandle handle)
    {
        var token = Capture(LaunchLockTelemetry.Attribution);
        if (token is null) return;
        LaunchLockChildCpuObservation? observation = null;
        try
        {
            lock (gate)
            {
                observation = new(handle, token, Completed);
                pending.Add(observation);
                observation.Register();
            }
        }
        catch
        {
            lock (gate)
            {
                if (observation is not null) { pending.Remove(observation); observation.Dispose(); }
                token.Exited(null);
            }
            ReportFailure();
        }
    }

    private void Completed(LaunchLockChildCpuObservation observation)
    {
        lock (gate)
        {
            if (!pending.Remove(observation)) return;
            observation.Credit();
            observation.Dispose();
        }
    }

    // Credit signaled handles synchronously before formatting, even if their callbacks are queued.
    internal void FlushExited()
    {
        lock (gate)
            foreach (var observation in pending.ToArray())
                if (observation.HasExited) Completed(observation);
    }

    internal static void ReportFailure()
    {
        try { Console.Error.WriteLine("launch-lock-cpu measurement_failed component=child_cpu_ms"); }
        catch { /* Diagnostics must not prevent a launch. */ }
    }

    internal sealed class Token(LaunchLockClassCpuLedger ledger, string key)
    {
        private int completed;
        internal void Exited(TimeSpan? cpu)
        {
            if (Interlocked.Exchange(ref completed, 1) != 0) return;
            ledger.CompleteChild(key, cpu);
        }
    }
}
