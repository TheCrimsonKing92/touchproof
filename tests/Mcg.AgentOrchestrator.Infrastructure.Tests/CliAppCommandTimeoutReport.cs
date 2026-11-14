using System.Diagnostics;
using System.Globalization;
using System.Text;

// Owns process observation and timeout diagnostics; successful output remains complete.
internal static class CliAppCommandTimeoutReport
{
    internal const int StreamTailCharacters = 16384;
    internal const string TruncationMarker = "[... truncated ...]";
    internal const int TerminationBoundMilliseconds = 2000;
    internal const int DrainBoundMilliseconds = 1000;

    internal static (int ExitCode, string Stdout, string Stderr) Run(
        ProcessStartInfo startInfo, IReadOnlyList<string> commandArguments,
        int executionTimeoutMilliseconds, Action<Process>? started = null, Readiness? readiness = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(executionTimeoutMilliseconds);
        readiness?.Begin();
        var elapsed = Stopwatch.StartNew();
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start app process.");
        var pid = process.Id;
        if (startInfo.RedirectStandardInput)
            process.StandardInput.Close();

        var stdout = new StreamCapture(readiness, isError: false);
        var stderr = new StreamCapture(readiness, isError: true);
        var outputRead = stdout.PumpAsync(process.StandardOutput);
        var errorRead = stderr.PumpAsync(process.StandardError);
        ObserveFault(outputRead);
        ObserveFault(errorRead);
        var drains = Task.WhenAll(outputRead, errorRead);
        ObserveFault(drains);
        started?.Invoke(process);
        var deadline = elapsed;
        if (readiness is not null)
        {
            try
            {
                // The caller guards this phase for hangs; only capture observations start the deadline.
                Task.WhenAny(readiness.Observed, drains).GetAwaiter().GetResult();
                if (!readiness.Observed.IsCompleted)
                {
                    drains.GetAwaiter().GetResult();
                    throw new InvalidOperationException(
                        "Readiness was not observed before both captured streams ended.\n" +
                        $"{stdout.Describe("Standard output")}\n{stderr.Describe("Standard error")}");
                }
                readiness.Observed.GetAwaiter().GetResult();
                deadline = Stopwatch.StartNew();
            }
            catch
            {
                StopAndDrain(process, drains);
                throw;
            }
        }
        if (process.WaitForExit(Remaining(executionTimeoutMilliseconds, deadline)))
        {
            drains.GetAwaiter().GetResult();
            return (process.ExitCode, stdout.FullText, stderr.FullText);
        }

        var terminationStatus = StopAndDrain(process, drains);
        var outputReport = stdout.Describe("Standard output");
        var errorReport = stderr.Describe("Standard error");
        elapsed.Stop();
        throw new TimeoutException(
            $"App command did not exit within {executionTimeoutMilliseconds} ms.\n" +
            $"Command arguments: {(commandArguments.Count == 0 ? "(none)" : string.Join(" ", commandArguments.Select(QuoteArgument)))}\n" +
            $"PID: {pid.ToString(CultureInfo.InvariantCulture)}\n" +
            $"Elapsed milliseconds: {elapsed.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture)}\n" +
            $"Termination: {terminationStatus}\n" +
            $"Drain: {(drains.IsCompletedSuccessfully ? "complete" : "incomplete")}\n" +
            $"{outputReport}\n{errorReport}");
    }

    private static string StopAndDrain(Process process, Task drains)
    {
        // Kill(true) can itself block, so termination and exit observation share one deadline.
        var terminationClock = Stopwatch.StartNew();
        var termination = Task.Factory.StartNew(() => Terminate(process, terminationClock),
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        ObserveFault(termination);
        WaitForTerminal(termination, Remaining(TerminationBoundMilliseconds, terminationClock));
        var terminationStatus = termination.IsCompletedSuccessfully ? termination.Result : "unconfirmed";

        // Both pumps share a single drain budget. Their buffers retain data even without EOF.
        WaitForTerminal(drains, DrainBoundMilliseconds);
        return terminationStatus;
    }

    internal static string RenderTail(string text)
    {
        if (text.Length == 0)
            return "(empty)";
        if (text.Length <= StreamTailCharacters)
            return text;
        var start = text.Length - (StreamTailCharacters - TruncationMarker.Length);
        if (char.IsLowSurrogate(text[start]))
            start++;
        return TruncationMarker + text[start..];
    }

    private static string Terminate(Process process, Stopwatch clock)
    {
        string? failure = null;
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) { failure = ex.GetType().Name; }

        var status = "unconfirmed";
        try
        {
            if (process.WaitForExit(Remaining(TerminationBoundMilliseconds, clock)))
                status = "exited";
        }
        catch (Exception ex) { failure ??= ex.GetType().Name; }
        return failure is null ? status : $"{status} ({failure})";
    }

    private static int Remaining(int milliseconds, Stopwatch clock) =>
        (int)Math.Max(0, milliseconds - clock.ElapsedMilliseconds);

    // WhenAny observes terminal state without replacing the primary timeout with a task fault.
    private static void WaitForTerminal(Task task, int milliseconds) =>
        Task.WhenAny(task).Wait(milliseconds);

    private static void ObserveFault(Task task) =>
        _ = task.ContinueWith(faulted => { _ = faulted.Exception; }, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    private static string QuoteArgument(string argument) =>
        argument.Length == 0 || argument.Any(char.IsWhiteSpace) || argument.Contains('"')
            ? "\"" + argument.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\""
            : argument;

    // A single run owns this condition; each required line belongs to its designated stream.
    internal sealed class Readiness(string outputLine, string errorLine)
    {
        private readonly object _sync = new();
        private readonly LineCondition _output = new(outputLine);
        private readonly LineCondition _error = new(errorLine);
        private readonly TaskCompletionSource _observed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _begun;

        internal Task Observed => _observed.Task;

        internal void Begin()
        {
            lock (_sync)
            {
                if (_begun)
                    throw new InvalidOperationException("A readiness condition cannot be reused across runs.");
                _begun = true;
            }
            ObserveFault(Observed);
        }

        internal void Observe(bool isError, ReadOnlySpan<char> text)
        {
            lock (_sync)
            {
                (isError ? _error : _output).Observe(text);
                if (_output.Observed && _error.Observed)
                    _observed.TrySetResult();
            }
        }

        internal void Fail(Exception error) => _observed.TrySetException(error);

        private sealed class LineCondition(string expected)
        {
            private readonly StringBuilder _line = new();
            private bool _tooLong;
            internal bool Observed { get; private set; }

            internal void Observe(ReadOnlySpan<char> text)
            {
                foreach (var character in text)
                {
                    if (Observed) return;
                    if (character == '\n')
                    {
                        if (_line.Length > 0 && _line[^1] == '\r')
                            _line.Length--;
                        Observed = !_tooLong && _line.ToString().Equals(expected, StringComparison.Ordinal);
                        _line.Clear();
                        _tooLong = false;
                    }
                    else if (_line.Length <= expected.Length)
                    {
                        // At most the expected line plus an optional CR is retained across chunks.
                        _line.Append(character);
                    }
                    else
                    {
                        _tooLong = true;
                    }
                }
            }
        }
    }

    private sealed class StreamCapture(Readiness? readiness, bool isError)
    {
        private readonly object _sync = new();
        private readonly StringBuilder _tail = new();
        private StringBuilder? _full = new();
        private long _charactersObserved;
        private bool _complete;
        private string? _failure;

        internal string FullText
        {
            get { lock (_sync) return _full!.ToString(); }
        }

        internal async Task PumpAsync(StreamReader reader)
        {
            var buffer = new char[4096];
            try
            {
                while (true)
                {
                    var count = await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false);
                    lock (_sync)
                    {
                        if (count == 0)
                        {
                            _complete = true;
                            return;
                        }
                        _charactersObserved += count;
                        _full?.Append(buffer, 0, count);
                        _tail.Append(buffer, 0, count);
                        readiness?.Observe(isError, buffer.AsSpan(0, count));
                        // Keep one overflow unit so RenderTail can identify discarded content.
                        var excess = _tail.Length - (StreamTailCharacters + 1);
                        if (excess > 0)
                            _tail.Remove(0, excess);
                    }
                }
            }
            catch (Exception ex)
            {
                lock (_sync) _failure = ex.GetType().Name;
                readiness?.Fail(ex);
                throw;
            }
        }

        internal string Describe(string label)
        {
            lock (_sync)
            {
                // A pump that outlives the drain deadline must stop retaining full output.
                _full = null;
                var state = _complete ? "complete" : "incomplete";
                if (_failure is not null)
                    state += $" ({_failure})";
                var truncation = _charactersObserved > StreamTailCharacters ? "truncated" : "untruncated";
                return $"{label} [{state}, {truncation}, {_charactersObserved.ToString(CultureInfo.InvariantCulture)} chars observed]:\n" +
                    RenderTail(_tail.ToString());
            }
        }
    }
}
