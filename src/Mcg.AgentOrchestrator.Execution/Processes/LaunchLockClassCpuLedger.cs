namespace Mcg.AgentOrchestrator.Infrastructure;

// Process-lifetime CPU totals. Membership counts tests, but each distinct class gets one share.
internal sealed class LaunchLockClassCpuLedger
{
    internal static LaunchLockClassCpuLedger Live { get; } = new();
    private readonly object gate = new();
    private readonly Dictionary<string, Counters> classes = new(StringComparer.Ordinal);
    private bool inProcessIncomplete;

    internal static string Normalize(string? key) =>
        string.IsNullOrEmpty(key) || key == "(none)" ? "unattributed" : key;

    private Counters Get(string? key)
    {
        key = Normalize(key);
        if (!classes.TryGetValue(key, out var counters)) classes.Add(key, counters = new());
        return counters;
    }

    internal void Enter(string? key)
    {
        lock (gate)
        {
            var counters = Get(key);
            counters.Active++;
            counters.Touched = true;
        }
    }

    internal void Exit(string? key)
    {
        lock (gate)
        {
            var counters = Get(key);
            if (counters.Active <= 0) throw new InvalidOperationException("CPU class activity ended without a matching start.");
            counters.Active--;
        }
    }

    internal void Apportion(double milliseconds)
    {
        lock (gate)
        {
            if (!double.IsFinite(milliseconds) || milliseconds < 0)
            {
                inProcessIncomplete = true;
                return;
            }
            var participants = 0;
            foreach (var counters in classes.Values)
                if (counters.Touched) participants++;
            if (participants == 0) Get(null).InProcess += milliseconds;
            else
            {
                var share = milliseconds / participants;
                foreach (var counters in classes.Values)
                {
                    if (counters.Touched) counters.InProcess += share;
                    counters.Touched = counters.Active > 0;
                }
            }
        }
    }

    internal void MarkInProcessIncomplete()
    {
        lock (gate) inProcessIncomplete = true;
    }

    internal void BeginChild(string key)
    {
        lock (gate) Get(key).PendingChildren++;
    }

    internal void CompleteChild(string key, TimeSpan? cpu)
    {
        lock (gate)
        {
            var counters = Get(key);
            counters.PendingChildren--;
            if (cpu is null || cpu.Value < TimeSpan.Zero) counters.ChildIncomplete = true;
            else counters.Child += cpu.Value.TotalMilliseconds;
        }
    }

    internal string[] FormatCpuByClass(int processId)
    {
        lock (gate)
        {
            if (inProcessIncomplete) return [];
            return classes.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Where(pair => !pair.Value.ChildIncomplete && pair.Value.PendingChildren == 0)
                .Select(pair => FormattableString.Invariant(
                    $"LAUNCH_LOCK_CPU_BY_CLASS pid={processId} class={LaunchLockTelemetry.FormatKey(pair.Key)} child_cpu_ms={pair.Value.Child:F3} inproc_cpu_ms_apportioned={pair.Value.InProcess:F3}"))
                .ToArray();
        }
    }

    private sealed class Counters
    {
        internal int Active;
        internal bool Touched;
        internal int PendingChildren;
        internal bool ChildIncomplete;
        internal double Child;
        internal double InProcess;
    }
}
