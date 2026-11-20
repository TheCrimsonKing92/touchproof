using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;

internal static class DependencyIndexMeasurementProbe
{
    public static void RequireCheckpointInput() =>
        Assert.SkipUnless(
            Environment.GetEnvironmentVariable("MCG_TEST_INPUT_DEPENDENCY_INDEX_CHECKPOINT") == "1",
            "The Reviewer enables the dependency index checkpoint with Invoke-TestSummary.ps1 -TestInput MCG_TEST_INPUT_DEPENDENCY_INDEX_CHECKPOINT=1.");

    public static void Measure(string arm, Func<Assembly, int> evaluate)
    {
        RequireCheckpointInput();
        var assembly = typeof(GoalMonitoringQuery).Assembly;
        var stopwatch = Stopwatch.StartNew();
        var violations = evaluate(assembly);
        stopwatch.Stop();

        var counters = new MemoryCounters { Size = (uint)Marshal.SizeOf<MemoryCounters>() };
        if (!GetProcessMemoryInfo(GetCurrentProcess(), ref counters, counters.Size))
            throw new Win32Exception(Marshal.GetLastWin32Error());

        Save(arm, new
        {
            Arm = arm,
            PeakPrivateBytes = (ulong)counters.PeakPagefileUsage,
            WallMilliseconds = stopwatch.Elapsed.TotalMilliseconds,
            Violations = violations,
            ProcessId = Environment.ProcessId,
            CollectedUtc = DateTimeOffset.UtcNow,
            Machine = Environment.MachineName,
            OS = RuntimeInformation.OSDescription,
            Runtime = RuntimeInformation.FrameworkDescription,
            Configuration = assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration,
            AppModuleVersionId = assembly.ManifestModule.ModuleVersionId
        });
    }

    public static void Save(string arm, object receipt)
    {
        var output = Path.Combine(VerifiedRepositoryRoot.Find(), ".scratch", "dependency-index-checkpoint");
        Directory.CreateDirectory(output);
        var path = Path.Combine(output, $"{arm}-{Environment.ProcessId}-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(receipt, new JsonSerializerOptions { WriteIndented = true }));
    }

    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentProcess();

    [DllImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessMemoryInfo(nint process, ref MemoryCounters counters, uint size);

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryCounters
    {
        public uint Size;
        public uint PageFaultCount;
        public nuint PeakWorkingSetSize;
        public nuint WorkingSetSize;
        public nuint QuotaPeakPagedPoolUsage;
        public nuint QuotaPagedPoolUsage;
        public nuint QuotaPeakNonPagedPoolUsage;
        public nuint QuotaNonPagedPoolUsage;
        public nuint PagefileUsage;
        public nuint PeakPagefileUsage;
    }
}
