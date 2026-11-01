using System.Diagnostics;
using System.Globalization;
using Mcg.AgentOrchestrator.App.Orchestration;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class CliTimingRecorder
{
    internal const string StartedVariable = "MCG_CLI_LAUNCHER_STARTED_UTC";
    internal const string PathVariable = "MCG_CLI_LAUNCHER_PATH";

    internal static void Install(IReadOnlyList<string> args)
    {
        try
        {
            var watch = Stopwatch.StartNew();
            var path = Environment.GetEnvironmentVariable(PathVariable);
            var started = Environment.GetEnvironmentVariable(StartedVariable);
            // Launcher metadata describes this invocation only; children must not inherit it.
            Environment.SetEnvironmentVariable(PathVariable, null);
            Environment.SetEnvironmentVariable(StartedVariable, null);
            // Direct/internal invocations preserve their noncreating workspace contract.
            if (path is null) return;
            var verb = args.Count == 0 ? "" : args[0];
            var root = OrchestratorWorkspace.ResolveRepoRoot();
            AppDomain.CurrentDomain.ProcessExit += (_, _) =>
            {
                try
                {
                    var completed = DateTimeOffset.UtcNow;
                    var elapsed = watch.Elapsed.TotalMilliseconds;
                    var directory = OrchestratorWorkspace.ForDirectory(root).LogDirectory;
                    Write(directory, verb, path, started, completed, elapsed, Environment.ExitCode);
                }
                catch { /* Telemetry never changes command output or results. */ }
            };
        }
        catch { /* Including environment access and exit-handler registration. */ }
    }

    internal static void Write(string directory, string verb, string? path, string? started,
        DateTimeOffset completed, double appElapsedMs, int exitCode,
        Func<DateTimeOffset>? processStarted = null)
    {
        try
        {
            DateTimeOffset? launcherStarted = DateTimeOffset.TryParse(started, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var parsed) ? parsed : null;
            // The adopted route avoids spawning a clock process; measure from the App process start.
            if (path == "fast" && string.IsNullOrEmpty(started))
                launcherStarted = (processStarted ?? ReadProcessStarted)();
            var valid = (path is "fast" or "fresh" or "built") && launcherStarted.HasValue;
            double? launcherElapsed = valid
                ? Math.Max(0, (completed - launcherStarted!.Value).TotalMilliseconds)
                : null;
            CliTimingLogWriter.Append(directory, new
            {
                timestamp = completed.ToUniversalTime(), verb,
                launcher_path = valid ? path : null,
                launcher_elapsed_ms = launcherElapsed,
                app_elapsed_ms = Math.Max(0, appElapsedMs), exit_code = exitCode
            });
        }
        catch { }
    }

    private static DateTimeOffset ReadProcessStarted()
    {
        using var process = Process.GetCurrentProcess();
        return new DateTimeOffset(process.StartTime.ToUniversalTime());
    }
}
