using System.ComponentModel;
using System.Diagnostics;

internal sealed record CliChildProcessResult(int ExitCode, string StandardOutput, string StandardError);

internal static class CliChildProcessRunner
{
    internal static readonly TimeSpan DefaultHangGuard = TimeSpan.FromSeconds(120);

    internal static CliChildProcessResult Run(
        ProcessStartInfo startInfo,
        TimeSpan? hangGuard = null,
        Action<Process>? started = null) =>
        RunAsync(startInfo, hangGuard, started).GetAwaiter().GetResult();

    internal static async Task<CliChildProcessResult> RunAsync(
        ProcessStartInfo startInfo,
        TimeSpan? hangGuard = null,
        Action<Process>? started = null)
    {
        var command = string.Join(" ", new[] { startInfo.FileName }
            .Concat(startInfo.ArgumentList.Select(QuoteArgument)));
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Could not start CLI child: {command}");
        if (startInfo.RedirectStandardInput)
            process.StandardInput.Close();

        long stdoutBytes = 0, stderrBytes = 0;
        var outputTask = startInfo.RedirectStandardOutput
            ? ReadOutput(process.StandardOutput, count => Interlocked.Add(ref stdoutBytes, count))
            : Task.FromResult(string.Empty);
        var errorTask = startInfo.RedirectStandardError
            ? ReadOutput(process.StandardError, count => Interlocked.Add(ref stderrBytes, count))
            : Task.FromResult(string.Empty);
        started?.Invoke(process);

        var clock = Stopwatch.StartNew();
        var watch = ChildProgressWatch.ForProcess(process,
            () => Interlocked.Read(ref stdoutBytes), () => Interlocked.Read(ref stderrBytes),
            hangGuard ?? DefaultHangGuard, () => clock.Elapsed);
        var exit = process.WaitForExitAsync();
        watch.Observe();
        while (!exit.IsCompleted)
        {
            await Task.WhenAny(exit, Task.Delay(ChildProgressWatch.SamplingSlice)).ConfigureAwait(false);
            if (exit.IsCompleted || process.HasExited) break;
            var stall = watch.Observe();
            if (stall is null) continue;
            var killed = false;
            if (!process.HasExited)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                    killed = true;
                }
                catch (InvalidOperationException) when (process.HasExited)
                {
                    // The child exited between the check and the kill.
                }
                catch (Win32Exception ex)
                {
                    throw new TimeoutException($"CLI child {command} did not exit; {stall}; process-tree termination failed: {ex.Message}", ex);
                }
            }
            await process.WaitForExitAsync().ConfigureAwait(false);
            var outputAfterKill = await outputTask.ConfigureAwait(false);
            var errorAfterKill = await errorTask.ConfigureAwait(false);
            if (killed)
                throw new TimeoutException($"CLI child {command} did not exit; {stall}. stdout={outputAfterKill} stderr={errorAfterKill}");
            return new CliChildProcessResult(process.ExitCode, outputAfterKill, errorAfterKill);
        }

        await exit.ConfigureAwait(false);
        return new CliChildProcessResult(process.ExitCode,
            await outputTask.ConfigureAwait(false), await errorTask.ConfigureAwait(false));
    }

    private static Task<string> ReadOutput(StreamReader reader, Action<int> observed) =>
        Task.Factory.StartNew(() =>
        {
            using var bytes = new MemoryStream();
            var buffer = new byte[8192];
            int count;
            while ((count = reader.BaseStream.Read(buffer, 0, buffer.Length)) != 0)
            {
                observed(count);
                bytes.Write(buffer, 0, count);
            }
            bytes.Position = 0;
            using var decoder = new StreamReader(bytes, reader.CurrentEncoding, detectEncodingFromByteOrderMarks: true);
            return decoder.ReadToEnd();
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    private static string QuoteArgument(string argument) =>
        argument.Any(char.IsWhiteSpace) ? $"\"{argument}\"" : argument;
}
