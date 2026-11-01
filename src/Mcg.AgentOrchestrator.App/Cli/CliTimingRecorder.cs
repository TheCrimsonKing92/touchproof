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
        DateTimeOffset completed, double appElapsedMs, int exitCode)
    {
        try
        {
            var valid = (path is "fast" or "fresh" or "built") &&
                DateTimeOffset.TryParse(started, CultureInfo.InvariantCulture, DateTimeStyles.None, out _);
            double? launcherElapsed = valid
                ? Math.Max(0, (completed - DateTimeOffset.Parse(started!, CultureInfo.InvariantCulture)).TotalMilliseconds)
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
}
