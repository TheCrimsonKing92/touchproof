using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Mcg.AgentOrchestrator.Infrastructure;

// Process-wide snapshots spanning a class's first start and most recent completed end.
internal sealed class LaunchLockClassMemoryLedger(Func<long?> readHeapBytes, Func<long?> readPrivateBytes)
{
    private static readonly uint ProcessMemoryCountersSize = (uint)Marshal.SizeOf<ProcessMemoryCountersEx>();
    internal static LaunchLockClassMemoryLedger Live { get; } = new(
        () => GC.GetGCMemoryInfo().HeapSizeBytes, ReadPrivateBytes);

    private readonly object gate = new();
    private readonly Dictionary<string, ClassSamples> classes = new(StringComparer.Ordinal);

    internal void Enter(string? key)
    {
        lock (gate)
        {
            var name = LaunchLockClassCpuLedger.Normalize(key);
            if (!classes.TryGetValue(name, out var samples))
                classes.Add(name, samples = new() { Start = ReadSample() });
            samples.Active++;
        }
    }

    internal void Exit(string? key)
    {
        lock (gate)
        {
            if (!classes.TryGetValue(LaunchLockClassCpuLedger.Normalize(key), out var samples) || samples.Active == 0)
                return;

            samples.Active--;
            if (samples.Active == 0 && samples.Start.HasValue && ReadSample() is { } end)
                samples.End = end;
        }
    }

    internal string[] FormatMemoryByClass(int processId)
    {
        lock (gate)
        {
            var lines = new List<string>();
            foreach (var (name, samples) in classes.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                if (samples.Start is { } start && samples.End is { } end)
                    lines.Add(FormattableString.Invariant(
                        $"LAUNCH_LOCK_MEMORY_BY_CLASS pid={processId} class={LaunchLockTelemetry.FormatKey(name)} heap_start_bytes={start.Heap} heap_end_bytes={end.Heap} private_start_bytes={start.Private} private_end_bytes={end.Private}"));
            }
            return lines.ToArray();
        }
    }

    private (long Heap, long Private)? ReadSample()
    {
        var heap = ReadCounter(readHeapBytes);
        var privateBytes = ReadCounter(readPrivateBytes);
        return heap is >= 0 && privateBytes is >= 0 ? (heap.Value, privateBytes.Value) : null;
    }

    private static long? ReadCounter(Func<long?> reader)
    {
        try { return reader(); }
        catch (Exception) { return null; }
    }

    private static long? ReadPrivateBytes()
    {
        if (OperatingSystem.IsWindows())
            return K32GetProcessMemoryInfo(GetCurrentProcess(), out var counters, ProcessMemoryCountersSize)
                ? checked((long)counters.PrivateUsage) : null;

        using var process = Process.GetCurrentProcess();
        return process.PrivateMemorySize64;
    }

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool K32GetProcessMemoryInfo(IntPtr process, out ProcessMemoryCountersEx counters, uint size);

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessMemoryCountersEx
    {
        internal uint Size;
        internal uint PageFaultCount;
        internal nuint PeakWorkingSetSize;
        internal nuint WorkingSetSize;
        internal nuint QuotaPeakPagedPoolUsage;
        internal nuint QuotaPagedPoolUsage;
        internal nuint QuotaPeakNonPagedPoolUsage;
        internal nuint QuotaNonPagedPoolUsage;
        internal nuint PagefileUsage;
        internal nuint PeakPagefileUsage;
        internal nuint PrivateUsage;
    }

    private sealed class ClassSamples
    {
        internal int Active;
        internal (long Heap, long Private)? Start;
        internal (long Heap, long Private)? End;
    }
}
