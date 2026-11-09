using System.Diagnostics;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class WorktreeTreeDigest
{
    internal const string Algorithm = "wbc1:git-tree";

    internal static bool TryCompute(string worktreeRoot, out string digest, out string reason)
        => TryCompute(worktreeRoot, out digest, out reason, null);

    internal static bool TryCompute(string worktreeRoot, out string digest, out string reason,
        Action<string>? temporaryRootObserver)
        => TryCompute(worktreeRoot, out digest, out reason, out _, null, temporaryRootObserver);

    internal static bool TryCompute(string worktreeRoot, out string digest, out string reason,
        out WorktreeTreeDigestFailure? failure,
        Func<string, Dictionary<string, string>?, string[], WorktreeTreeDigestGitStep>? gitRunner = null,
        Action<string>? temporaryRootObserver = null,
        Func<string, string?>? environment = null)
    {
        digest = string.Empty;
        reason = "digest-unavailable";
        failure = null;
        var run = gitRunner ?? Run;
        string? temporaryRoot = null;
        try
        {
            temporaryRoot = Path.Combine(OrchestratorTempRoot.GetPurposeDirectory("worktree-digest"),
                "mcg-worktree-digest-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temporaryRoot);
            temporaryRootObserver?.Invoke(temporaryRoot);
            var objects = Path.Combine(temporaryRoot, "objects");
            Directory.CreateDirectory(objects);
            var readEnvironment = environment ?? Environment.GetEnvironmentVariable;
            var objectsDirectory = gitRunner is null
                ? WorktreeCleanTreeFastPath.TryLocateObjects(worktreeRoot, readEnvironment) : null;
            if (objectsDirectory is not null && WorktreeCleanTreeFastPath.TryMatchClean(
                worktreeRoot, readEnvironment, run, Volatile.Read(ref WorktreeCleanTreeFastPath.HeadTreeResolver), out var treeId))
            {
                digest = $"{Algorithm}:{treeId.ToLowerInvariant()}";
                reason = "matched";
                return true;
            }
            if (objectsDirectory is null)
            {
                var commonObjects = run(worktreeRoot, null, ["rev-parse", "--path-format=absolute", "--git-path", "objects"]);
                if (commonObjects.ExitCode != 0 || commonObjects.TimedOut || string.IsNullOrWhiteSpace(commonObjects.Output))
                {
                    failure = WorktreeTreeDigestFailure.FromStep("rev-parse", commonObjects);
                    return false;
                }
                objectsDirectory = commonObjects.Output.Trim();
            }
            var gitEnvironment = new Dictionary<string, string>
            {
                ["GIT_INDEX_FILE"] = Path.Combine(temporaryRoot, "index"),
                ["GIT_OBJECT_DIRECTORY"] = objects,
                ["GIT_ALTERNATE_OBJECT_DIRECTORIES"] = objectsDirectory,
                ["GIT_OPTIONAL_LOCKS"] = "0"
            };
            foreach (var args in new[] { new[] { "read-tree", "HEAD" }, new[] { "add", "-A" }, new[] { "write-tree" } })
            {
                var result = run(worktreeRoot, gitEnvironment, args);
                if (result.ExitCode != 0 || result.TimedOut)
                {
                    failure = WorktreeTreeDigestFailure.FromStep(args[0], result);
                    return false;
                }
                if (args[0] != "write-tree") continue;
                var hash = result.Output.Trim();
                if ((hash.Length != 40 && hash.Length != 64) || !hash.All(Uri.IsHexDigit))
                {
                    failure = WorktreeTreeDigestFailure.FromStep("tree-hash", result);
                    return false;
                }
                digest = $"{Algorithm}:{hash.ToLowerInvariant()}";
            }
            reason = "matched";
            return true;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            failure = WorktreeTreeDigestFailure.FromException(exception);
            return false;
        }
        finally
        {
            try { if (temporaryRoot is not null) Directory.Delete(temporaryRoot, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static WorktreeTreeDigestGitStep Run(string root, Dictionary<string, string>? environment, params string[] args)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var arg in args)
            start.ArgumentList.Add(arg);
        if (environment is not null)
            foreach (var (key, value) in environment) start.Environment[key] = value;
        var useConsolePolicy = GitCli.ApplyLaunchSettings(start, readCommand: false);
        return RunProcess(start, 60_000, PipeDrain.DefaultTimeoutMilliseconds, useConsolePolicy);
    }

    internal static WorktreeTreeDigestGitStep RunProcess(
        ProcessStartInfo start, int processTimeoutMilliseconds, int drainTimeoutMilliseconds,
        bool useConsolePolicy = false)
    {
        using var process = (useConsolePolicy
            ? HandleListProcessLauncher.StartSuppressed(start)
            : HandleListProcessLauncher.Start(start)) ?? throw new IOException("Could not start git.");
        // Dedicated drains keep output independent of thread-pool availability without blocking the exit wait.
        var outputDrain = PipeDrain.Start(process.StandardOutput, "digest-stdout-drain");
        var errorDrain = PipeDrain.Start(process.StandardError, "digest-stderr-drain");
        if (!process.WaitForExit(processTimeoutMilliseconds))
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            return new(-1, string.Empty,
                WorktreeTreeDigestFailure.FirstLine(errorDrain.Text), true, TimeoutKind: "wait");
        }
        var drainDeadline = Environment.TickCount64 + drainTimeoutMilliseconds;
        var outputDrained = outputDrain.Join(drainDeadline);
        var errorDrained = errorDrain.Join(drainDeadline);
        if (!outputDrained || !errorDrained)
            return new(-1, string.Empty,
                WorktreeTreeDigestFailure.FirstLine(errorDrain.Text), true, TimeoutKind: "drain");
        return new(process.ExitCode, outputDrain.Text, WorktreeTreeDigestFailure.FirstLine(errorDrain.Text), false);
    }
}
