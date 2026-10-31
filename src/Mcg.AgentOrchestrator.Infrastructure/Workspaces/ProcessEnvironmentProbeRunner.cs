using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace Mcg.AgentOrchestrator.Infrastructure;

/// <summary>Runs literal argv without a shell. Both streams drain concurrently into a bounded output tail.</summary>
public sealed class ProcessEnvironmentProbeRunner : IEnvironmentProbeRunner
{
    public static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(2);
    public const int OutputTailLimit = 400;
    private const int CaptureLimit = 65536;
    private readonly TimeSpan _probeTimeout;
    private readonly TimeProvider _timeProvider;

    public ProcessEnvironmentProbeRunner() : this(ProbeTimeout) { }

    public ProcessEnvironmentProbeRunner(TimeSpan timeout, TimeProvider? timeProvider = null)
    {
        if (timeout <= TimeSpan.Zero || timeout > ProbeTimeout)
            throw new ArgumentOutOfRangeException(nameof(timeout), "Probe execution must be bounded to at most 20 seconds.");
        _probeTimeout = timeout;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<EnvironmentProbeResult> RunAsync(EnvironmentToolRequirement tool, ConductorChildEnvironment environment,
        string workingDirectory, CancellationToken cancellationToken = default)
    {
        using var deadline = new CancellationTokenSource(_probeTimeout, _timeProvider);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        var tail = new StringBuilder();
        var captured = new StringBuilder();
        var captureLimited = false;
        using var process = new Process();
        try
        {
            var command = ResolveCommand(tool.Command, environment, workingDirectory);
            if (command is null) return new(tool, false, null, "Executable was not found in the child environment.", "not-found");
            process.StartInfo = new ProcessStartInfo(command)
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
                RedirectStandardError = true, RedirectStandardInput = true, WorkingDirectory = workingDirectory };
            environment.Apply(process.StartInfo);
            foreach (var argument in tool.Arguments) process.StartInfo.ArgumentList.Add(argument);
            if (!process.Start()) return new(tool, false, null, "Process did not start.", "start-failed");
            process.StandardInput.Close();
            var stdout = Drain(process.StandardOutput);
            var stderr = Drain(process.StandardError);
            string? interrupted = null;
            try
            {
                // Kill on the exit deadline before waiting for pipe EOF: anonymous-pipe reads
                // can ignore cancellation while the child or a descendant retains a writer.
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                interrupted = cancellationToken.IsCancellationRequested ? "cancelled" : "timeout";
                StopTree();
            }
            try
            {
                // Reaping and both drains share one independent cleanup bound, including when
                // the root has exited but a descendant still holds a redirected pipe open.
                await Task.WhenAll(process.WaitForExitAsync(), stdout, stderr)
                    .WaitAsync(CleanupTimeout).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                StopTree();
                return new(tool, false, null, ReadTail(), interrupted ?? "output-drain-timeout");
            }
            if (interrupted is not null) return new(tool, false, null, ReadTail(), interrupted);
            if (!stdout.Result || !stderr.Result)
                return new(tool, false, process.ExitCode, ReadTail(), "output-read-failed");
            var output = ReadTail();
            var passed = process.ExitCode == 0 && !captureLimited && (tool.OutputPattern is null ||
                Regex.IsMatch(captured.ToString(), tool.OutputPattern, RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, TimeSpan.FromSeconds(1)));
            return new(tool, passed, process.ExitCode, output, passed ? "pass" : captureLimited ? "output-limit" : "failed");
        }
        catch (RegexMatchTimeoutException)
        {
            return new(tool, false, process.ExitCode, ReadTail(), "output-pattern-timeout");
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or InvalidOperationException or ArgumentException or NotSupportedException)
        {
            return new(tool, false, null, ex.Message.Length > OutputTailLimit ? ex.Message[^OutputTailLimit..] : ex.Message, "start-failed");
        }
        finally
        {
            StopTree();
        }

        void StopTree()
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        }

        string ReadTail() { lock (tail) return tail.ToString().Replace('\r', ' ').Replace('\n', ' '); }
        async Task<bool> Drain(StreamReader reader)
        {
            var buffer = new char[512];
            try
            {
                while (await reader.ReadAsync(buffer).ConfigureAwait(false) is var count && count > 0)
                    lock (tail)
                    {
                        tail.Append(buffer, 0, count);
                        if (tail.Length > OutputTailLimit) tail.Remove(0, tail.Length - OutputTailLimit);
                        var remaining = CaptureLimit - captured.Length;
                        captured.Append(buffer, 0, Math.Min(count, remaining));
                        if (count > remaining) captureLimited = true;
                    }
                return true;
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
            {
                return false;
            }
        }
    }

    private static string? ResolveCommand(string command, ConductorChildEnvironment environment, string directory)
    {
        if (command == "crosswalk")
        {
            environment.Variables.TryGetValue("MCG_ORCHESTRATOR_DATA", out var root);
            if (string.IsNullOrWhiteSpace(root))
            {
                environment.Variables.TryGetValue("LOCALAPPDATA", out var local);
                if (string.IsNullOrWhiteSpace(local)) return null;
                root = Path.Combine(local, "Mcg.AgentOrchestrator");
            }
            var crosswalk = Path.Combine(root, "tools", "crosswalk", "crosswalk.exe");
            return File.Exists(crosswalk) ? crosswalk : null;
        }
        if (command.Contains('/') || command.Contains('\\'))
        {
            var path = Path.GetFullPath(command, directory);
            return File.Exists(path) ? path : null;
        }
        if (OperatingSystem.IsWindows() && !Path.HasExtension(command))
            return AcceptanceBareCommandResolver.Resolve(command, environment.Variables, directory);
        environment.Variables.TryGetValue("PATH", out var pathValue);
        return (pathValue ?? "").Split(Path.PathSeparator).Where(entry => entry.Length > 0)
            .Select(entry => Path.GetFullPath(Path.Combine(entry.Trim('"'), command), directory)).FirstOrDefault(File.Exists);
    }
}
