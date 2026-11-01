using System.Diagnostics;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class WorkerNodeDependencyPreparation
{
    internal static bool HasOwnNodeModules(string worktree)
    {
        var modules = new DirectoryInfo(Path.Combine(worktree, "node_modules"));
        if (!modules.Exists) return false;
        var target = modules.ResolveLinkTarget(returnFinalTarget: true);
        return target is null || IsWithin(target.FullName, worktree);
    }

    private static bool IsWithin(string path, string root) =>
        Path.GetFullPath(path).StartsWith(
            Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
            Path.DirectorySeparatorChar,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    internal static void Prepare(ProcessStartInfo worker, DispatchProcessHost.DispatchRunParameters parameters,
        Func<ProcessStartInfo, WorkerProcessRunResult>? runner = null)
    {
        if (parameters.NodeDependencyInstall is not { } install)
            return;

        var sandboxRoot = DispatchProcessHost.ResolveSandboxRoot(parameters);
        // Shared across per-dispatch sandbox instances: an interrupted install must not make its
        // partially populated node_modules qualify as an already prepared dependency tree.
        var incomplete = Path.Combine(parameters.WorkingDirectory, ".mcg-sandbox", "node-install-incomplete");
        try
        {
            if (!File.Exists(incomplete) && HasOwnNodeModules(parameters.WorkingDirectory)) return;
            NodeToolCacheEnvironment.Apply(worker.Environment, sandboxRoot);
            Directory.CreateDirectory(worker.Environment["TEMP"]!);
            using var preparationLease = new FileStream(incomplete, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            // Use the same local-only exclude mechanism as the sandbox, before generating dependencies.
            // This does not change any permission on the shared git metadata.
            var excluded = LocalGitExclude.TryAppendEntries(parameters.WorkingDirectory, ["/node_modules/"]);
            var gitPath = Path.Combine(parameters.WorkingDirectory, ".git");
            if (!excluded && (File.Exists(gitPath) || Directory.Exists(gitPath)))
                throw new IOException("Could not exclude prepared node_modules from the worktree's git status.");

            static string Quote(string value) => "'" + value.Replace("'", "''") + "'";
            var modules = Path.Combine(parameters.WorkingDirectory, "node_modules");
            var command = "$ErrorActionPreference = 'Stop'; ";
            if (OperatingSystem.IsWindows() && parameters.SandboxLowIntegrity)
                command += $". {Quote(Path.Combine(sandboxRoot, "drop-to-low.ps1"))}; ";
            // Remove only the link itself, under the worker's restrictions. Never traverse its target.
            command += $"if ([IO.DirectoryInfo]::new({Quote(modules)}).LinkTarget) {{ [IO.Directory]::Delete({Quote(modules)}) }}; ";
            command += "& " + Quote(install.Command) + " " + string.Join(" ", install.Arguments.Select(Quote)) +
                "; exit $LASTEXITCODE";
            var startInfo = WorkerProcessRunner.BuildPowerShellStartInfo(command, parameters.WorkingDirectory);
            startInfo.Environment.Clear();
            foreach (var entry in worker.Environment)
                startInfo.Environment[entry.Key] = entry.Value;
            var result = (runner ?? (info => NodeDependencyInstallProcess.Run(info, sandboxRoot)))(startInfo);
            if (result.ExitCode != 0 || result.TimedOut || result.CaptureLimited ||
                !HasOwnNodeModules(parameters.WorkingDirectory))
                throw new InvalidOperationException(
                    $"Node install exit={result.ExitCode} timed_out={result.TimedOut}; " +
                    $"{result.StandardOutput}\n{result.StandardError}");
            preparationLease.Dispose();
            File.Delete(incomplete);
        }
        catch (Exception ex) when (ex is not WorkerSandboxRefusedException)
        {
            throw new WorkerSandboxRefusedException("node-dependency-preparation-failed", parameters.WorkingDirectory, ex);
        }
    }
}
