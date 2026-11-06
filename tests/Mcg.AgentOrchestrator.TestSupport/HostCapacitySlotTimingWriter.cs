using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Xunit;

/// <summary>Best-effort telemetry for completed host-capacity slot uses.</summary>
public static class HostCapacitySlotTimingWriter
{
    public const string VariableName = "MCG_TEST_SLOT_TIMING_FILE";
    public const int GitCallerLimit = 20;
    private static readonly object AppendLock = new();

    public static string CurrentTestName()
    {
        try
        {
            var context = TestContext.Current;
            return context.TestClass?.TestClassName is { } className && context.TestMethod?.MethodName is { } methodName
                ? className + "." + methodName : "<unknown>";
        }
        catch (Exception) { return "<unknown>"; }
    }

    public static void TryAppend(string destination, string? testName, double waitMs, double heldMs, int slotCount,
        HostCapacitySlotGitCensus.GitLaunchDelta? git = null)
    {
        try
        {
            var missingIdentity = string.IsNullOrWhiteSpace(testName) || testName == "<unknown>";
            if (missingIdentity) Diagnose(destination, "current test identity unavailable; recording <unknown>");
            var row = new Dictionary<string, object?>
            {
                ["test_name"] = missingIdentity ? "<unknown>" : testName,
                ["wait_ms"] = waitMs, ["held_ms"] = heldMs,
                ["slot_count"] = slotCount, ["process_id"] = Environment.ProcessId
            };
            if (git is not null)
            {
                row["git_launches"] = git.Launches;
                row["git_subcommands"] = git.Subcommands;
                var callers = (git.Callers ?? new Dictionary<string, int>())
                    .OrderByDescending(pair => pair.Value).ThenBy(pair => pair.Key, StringComparer.Ordinal)
                    .Take(GitCallerLimit).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
                callers["other"] = git.Launches - callers.Values.Sum();
                row["git_callers"] = callers;
                if (git.Timing is { Processes: > 0 } timing)
                {
                    row["git_start_ms"] = timing.StartMs;
                    row["git_wall_ms"] = timing.WallMs;
                    row["git_wall_ms_by_subcommand"] = timing.WallMsBySubcommand;
                    if (timing.CpuMs is { } cpuMs) row["git_cpu_ms"] = cpuMs;
                    if (timing.CpuUnavailable > 0) row["git_cpu_unavailable"] = timing.CpuUnavailable;
                }
            }
            var line = JsonSerializer.Serialize(row);
            // SharedJsonlFile also excludes concurrent writers across processes for the whole append.
            lock (AppendLock) SharedJsonlFile.AppendLine(destination, line);
        }
        catch (Exception error) { Diagnose(destination, error.GetType().Name + ": " + error.Message); }
    }

    internal static void Diagnose(string destination, string cause)
    {
        try { Console.Error.WriteLine($"Host-capacity slot timing '{destination}': {cause}"); }
        catch (Exception) { /* Telemetry must never change a test verdict, including stderr failures. */ }
    }
}
