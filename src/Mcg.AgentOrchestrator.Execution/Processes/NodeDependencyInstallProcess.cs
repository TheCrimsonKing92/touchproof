using System.Diagnostics;
using System.Text;

namespace Mcg.AgentOrchestrator.Infrastructure;

// Owns the short-lived install process and its retained diagnostics, never a worker task dispatch.
internal static class NodeDependencyInstallProcess
{
    internal static WorkerProcessRunResult Run(ProcessStartInfo startInfo, string sandboxRoot) =>
        RunAsync(startInfo, sandboxRoot).GetAwaiter().GetResult();

    private static async Task<WorkerProcessRunResult> RunAsync(ProcessStartInfo startInfo, string sandboxRoot)
    {
        var prefix = Path.Combine(sandboxRoot, "node-install-" + Guid.NewGuid().ToString("N"));
        var stdoutPath = prefix + ".out.log";
        var stderrPath = prefix + ".err.log";
        if (OperatingSystem.IsWindows())
            return await RunWindowsAsync(startInfo, stdoutPath, stderrPath).ConfigureAwait(false);

        await using var stdout = new FileStream(stdoutPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        await using var stderr = new FileStream(stderrPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        using var process = ProcessTreeGuiSuppression.Start(startInfo);
        OwnedProcessGroup? group = null;
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        using var drainCancellation = new CancellationTokenSource();
        var output = process.StandardOutput.BaseStream.CopyToAsync(stdout, drainCancellation.Token);
        var error = process.StandardError.BaseStream.CopyToAsync(stderr, drainCancellation.Token);
        var drains = Task.WhenAll(output, error);
        var timedOut = false;
        var captureLimited = false;
        try
        {
            group = OwnedProcessGroup.Attach(process);
            process.StandardInput.Close();
            try
            {
                await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested)
            {
                timedOut = true;
            }
        }
        finally
        {
            // Kill descendants even after shell exit so an inherited pipe cannot outlive preparation.
            try { WorkerProcessJobs.ReadAccountingAndDispose(group, kill: true, captureAccounting: false, out _); }
            finally
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            if (await Task.WhenAny(drains, Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(false) != drains)
            {
                captureLimited = true;
                drainCancellation.Cancel();
            }
            try { await drains.ConfigureAwait(false); }
            catch (OperationCanceledException) when (drainCancellation.IsCancellationRequested) { }
        }
        await stdout.FlushAsync().ConfigureAwait(false);
        await stderr.FlushAsync().ConfigureAwait(false);
        return new WorkerProcessRunResult(timedOut ? -1 : process.ExitCode,
            Tail(stdoutPath), Tail(stderrPath), timedOut, captureLimited);
    }

    private static async Task<WorkerProcessRunResult> RunWindowsAsync(
        ProcessStartInfo startInfo, string stdoutPath, string stderrPath)
    {
        // The existing suspended-launch primitive binds the process to its job before it can spawn
        // descendants and explicitly limits inherited handles to these capture files and null stdin.
        startInfo.RedirectStandardInput = startInfo.RedirectStandardOutput = startInfo.RedirectStandardError = false;
        using var launch = OwnedProcessGroup.StartSuspendedContainedWithFileCapture(startInfo, stdoutPath, stderrPath);
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var timedOut = false;
        var treeStopped = false;
        try
        {
            launch.Resume();
            await launch.WaitForOwnedExitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            timedOut = true;
        }
        finally
        {
            treeStopped = launch.Group.TerminateAndConfirmEmpty(TimeSpan.FromSeconds(5));
        }
        var exitCode = launch.TryReadOwnedExitCode();
        return new WorkerProcessRunResult(timedOut ? -1 : exitCode ?? -1,
            Tail(stdoutPath), Tail(stderrPath), timedOut, CaptureLimited: !treeStopped);
    }

    private static string Tail(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        stream.Seek(Math.Max(0, stream.Length - 16_384), SeekOrigin.Begin);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return $"diagnostics={path}\n{reader.ReadToEnd()}";
    }
}
