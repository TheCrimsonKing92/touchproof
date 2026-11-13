using System.Diagnostics;

internal static class ScriptFixtureProcessRunner
{
    internal sealed record Output(string Stdout, string Stderr);

    internal static Output AssertExited(Process process, string failsafeMessage)
    {
        long stdoutBytes = 0, stderrBytes = 0;
        var stdout = process.StartInfo.RedirectStandardOutput
            ? ReadOutput(process.StandardOutput, count => Interlocked.Add(ref stdoutBytes, count))
            : Task.FromResult(string.Empty);
        var stderr = process.StartInfo.RedirectStandardError
            ? ReadOutput(process.StandardError, count => Interlocked.Add(ref stderrBytes, count))
            : Task.FromResult(string.Empty);
        var clock = Stopwatch.StartNew();
        AssertExited(
            process,
            failsafeMessage,
            static (candidate, timeout) => candidate.WaitForExit(timeout),
            static candidate => candidate.Kill(entireProcessTree: true),
            ChildProgressWatch.ForProcess(process,
                () => Interlocked.Read(ref stdoutBytes), () => Interlocked.Read(ref stderrBytes),
                TimeSpan.FromMilliseconds(30_000), () => clock.Elapsed));
        return new(stdout.GetAwaiter().GetResult(), stderr.GetAwaiter().GetResult());
    }

    internal static void AssertExited(
        Process process,
        string failsafeMessage,
        Func<Process, int, bool> waitForExit,
        Action<Process> killTree,
        ChildProgressWatch watch)
    {
        watch.Observe();
        while (!waitForExit(process, (int)ChildProgressWatch.SamplingSlice.TotalMilliseconds))
        {
            var stall = watch.Observe();
            if (stall is null) continue;
            // Exit wins if it races the stall observation.
            if (waitForExit(process, 0)) return;
            try
            {
                killTree(process);
            }
            catch (InvalidOperationException)
            {
                // The child may have exited between the bounded wait and the kill.
            }

            waitForExit(process, 5_000);
            Xunit.Assert.True(false, $"{failsafeMessage} {stall}");
        }
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
}
