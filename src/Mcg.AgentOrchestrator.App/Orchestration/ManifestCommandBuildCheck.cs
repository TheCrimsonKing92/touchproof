using System.Diagnostics;
using Mcg.AgentOrchestrator.Infrastructure;
using static Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class ManifestCommandBuildCheck
{
    internal static readonly OrchestratorBuildCheckResult NoBuildDeclared = new(false, 0,
        "no build check declared for this project; the acceptance gate builds it");

    internal static bool IsNoBuildDeclared(OrchestratorBuildCheckResult result) => result == NoBuildDeclared;

    internal static OrchestratorBuildCheckResult Run(string worktree, string? projectHomeDirectory,
        TimeSpan timeout, Func<string[], string, TimeSpan, CommandResult> runner)
    {
        var elapsed = Stopwatch.StartNew();
        AcceptanceManifest manifest;
        try { manifest = AcceptanceManifest.Load(worktree, changedFiles: null, projectHomeDirectory); }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { return new(false, -1, "Could not load build-check manifest: " + exception.Message); }

        var summaries = new List<string>();
        var diagnostics = new List<string>();
        string Output(string? failure = null) => string.Join(Environment.NewLine,
            summaries.Concat(failure is null ? [] : new[] { failure }).Concat(diagnostics));
        var ran = false;
        foreach (var check in manifest.Checks ?? [])
        {
            if (!string.Equals(check.Type, "command", StringComparison.OrdinalIgnoreCase)) continue;
            string[] arguments;
            try { arguments = BuildCommandArguments(check); }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            { return new(false, -1, Output(exception.Message)); }

            var remaining = timeout - elapsed.Elapsed;
            if (remaining <= TimeSpan.Zero)
                return new(false, -1, Output($"Build check timed out after {timeout}."));
            CommandResult result;
            var command = string.Join(' ', arguments.Select(QuoteForDisplay));
            try { result = runner(arguments, worktree, remaining); }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            { return new(false, -1, Output($"Build check '{check.Name}' failed to launch: {command}: {exception.Message}")); }

            ran = true;
            summaries.Add($"{check.Name}: {command} (exit {result.ExitCode})");
            if (TailOutput(result.Output) is { } tail) diagnostics.Add($"{check.Name}: {tail}");
            if (result.TimedOut || elapsed.Elapsed >= timeout)
                return new(false, -1, Output($"Build check timed out after {timeout}."));
            if (result.ExitCode != 0) return new(true, result.ExitCode, Output());
        }
        return ran ? new(true, 0, Output()) : NoBuildDeclared;
    }

    internal static CommandResult RunCommand(string[] arguments, string worktree, TimeSpan timeout) =>
        AcceptanceProcessRunner.RunProcessAsync(arguments, worktree, timeout,
            forceUtf8ConsoleOutput: false, CancellationToken.None, resolveBareCommand: true).GetAwaiter().GetResult();
}
