using System.Diagnostics;
using System.Globalization;

internal sealed class TestChildProcessCapture : IDisposable
{
    private const int DefaultRetentionBytes = 4 * 1024 * 1024;
    internal static readonly TimeSpan CleanupMargin = TimeSpan.FromSeconds(5);
    private readonly Process _process;
    private readonly int _pid;
    private readonly ProcessStartInfo _startInfo;
    private readonly Func<Task, TimeSpan, bool> _wait;
    private readonly Action<Process> _stopTree;
    private readonly int _retentionBytes;
    private readonly TestChildProcessCaptureDiagnostics _diagnostics;
    private readonly Controls? _controls;
    private readonly Stopwatch _elapsed;
    private Stopwatch? _cleanup;
    private readonly Task _exit;
    private readonly Task<StreamCapture> _stdout;
    private readonly Task<StreamCapture> _stderr;
    private readonly TaskCompletionSource<string> _overflow = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _stopped;
    private long _stdoutBytes;
    private long _stderrBytes;
    private ChildProgressWatch.Stall? _stall;
    private bool _disposed;

    internal sealed record Result(int ExitCode, string Stdout, string Stderr);

    // Instance-scoped controls observe committed drain state; callbacks never run
    // under the diagnostic lock and cannot delay overflow publication/cleanup.
    internal sealed record Controls(
        Func<Stream, bool, Stream>? OpenStream = null,
        Action<bool, long, bool>? Progress = null);

    private sealed record StreamCapture(byte[] Bytes, long ObservedBytes);

    private TestChildProcessCapture(
        ProcessStartInfo startInfo,
        Process process,
        Stopwatch elapsed,
        Func<Task, TimeSpan, bool> wait,
        Action<Process> stopTree,
        int retentionBytes,
        Controls? controls)
    {
        _startInfo = startInfo;
        _process = process;
        _pid = process.Id;
        _elapsed = elapsed;
        _wait = wait;
        _stopTree = stopTree;
        _retentionBytes = retentionBytes;
        _controls = controls;
        _diagnostics = new(process.StandardOutput.CurrentEncoding, process.StandardError.CurrentEncoding);
        _stdout = Task.Factory.StartNew(
            () => Drain(process.StandardOutput.BaseStream, true),
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        _stderr = Task.Factory.StartNew(
            () => Drain(process.StandardError.BaseStream, false),
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        _exit = Task.Factory.StartNew(
            process.WaitForExit,
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    internal static Result Run(
        ProcessStartInfo startInfo,
        TimeSpan? hangBound = null,
        Func<ProcessStartInfo, Process?>? start = null,
        Func<Task, TimeSpan, bool>? wait = null,
        Action<Process>? stopTree = null,
        int retentionBytes = DefaultRetentionBytes,
        Controls? controls = null)
    {
        var bound = hangBound ?? TestHangGuard.Bound;
        if (bound < TimeSpan.FromSeconds(30))
            throw new ArgumentOutOfRangeException(nameof(hangBound), "The hang bound must be at least 30 seconds.");

        using var capture = Start(startInfo, start, wait, stopTree, retentionBytes, controls);
        return capture.Complete(bound);
    }

    internal static TestChildProcessCapture Start(
        ProcessStartInfo startInfo,
        Func<ProcessStartInfo, Process?>? start = null,
        Func<Task, TimeSpan, bool>? wait = null,
        Action<Process>? stopTree = null,
        int retentionBytes = DefaultRetentionBytes,
        Controls? controls = null)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        if (!startInfo.RedirectStandardOutput || !startInfo.RedirectStandardError)
            throw new ArgumentException("Both child streams must be redirected.", nameof(startInfo));
        if (retentionBytes < 1)
            throw new ArgumentOutOfRangeException(nameof(retentionBytes));

        var elapsed = Stopwatch.StartNew();
        var process = (start ?? Process.Start)(startInfo)
            ?? throw new InvalidOperationException($"Failed to start child {Describe(startInfo)}.");
        try
        {
            return new TestChildProcessCapture(startInfo, process, elapsed,
                wait ?? ((task, bound) => task.Wait(bound)),
                stopTree ?? (child => child.Kill(entireProcessTree: true)), retentionBytes, controls);
        }
        catch
        {
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            process.Dispose();
            throw;
        }
    }

    internal bool TryWaitForExit(TimeSpan bound)
    {
        if (!WaitWithProgress(_exit, bound))
        {
            StopOwnedTree();
            return false;
        }
        _exit.GetAwaiter().GetResult();
        return true;
    }

    internal Result Complete(TimeSpan? hangBound = null)
    {
        var bound = hangBound ?? TestHangGuard.Bound;
        var all = Task.WhenAll(_exit, _stdout, _stderr);
        var first = Task.WhenAny(all, _overflow.Task);
        if (!WaitWithProgress(first, bound))
            Fail("stdout/stderr or exit", bound);
        first.GetAwaiter().GetResult();
        if (_overflow.Task.IsCompletedSuccessfully)
            Fail(_overflow.Task.Result, bound);
        all.GetAwaiter().GetResult();

        var stdout = _stdout.Result;
        var stderr = _stderr.Result;
        if (stdout.ObservedBytes > _retentionBytes)
            Fail("stdout", bound);
        if (stderr.ObservedBytes > _retentionBytes)
            Fail("stderr", bound);

        return new Result(_process.ExitCode,
            Decode(stdout.Bytes, _process.StandardOutput.CurrentEncoding),
            Decode(stderr.Bytes, _process.StandardError.CurrentEncoding));
    }

    private StreamCapture Drain(Stream stream, bool isStdout)
    {
        using var retained = new MemoryStream();
        var buffer = new byte[8192];
        long observed = 0;
        int read;
        try
        {
            stream = _controls?.OpenStream?.Invoke(stream, isStdout) ?? stream;
            while ((read = stream.Read(buffer, 0, buffer.Length)) != 0)
            {
                observed += read;
                _diagnostics.Publish(isStdout, buffer.AsSpan(0, read));
                Interlocked.Exchange(ref (isStdout ? ref _stdoutBytes : ref _stderrBytes), observed);
                var keep = (int)Math.Min(read, Math.Max(0, _retentionBytes - retained.Length));
                retained.Write(buffer, 0, keep);
                if (observed > _retentionBytes)
                {
                    _diagnostics.Freeze();
                    _overflow.TrySetResult(isStdout ? "stdout" : "stderr");
                    StopOwnedTree();
                }
                _controls?.Progress?.Invoke(isStdout, observed, false);
            }
            _diagnostics.End(isStdout);
            _controls?.Progress?.Invoke(isStdout, observed, true);
        }
        catch (Exception error)
        {
            _diagnostics.End(isStdout, unavailable: true);
            _controls?.Progress?.Invoke(isStdout, observed, false);
            if (Volatile.Read(ref _stopped) == 0 || error is not (IOException or ObjectDisposedException))
                throw;
        }
        return new StreamCapture(retained.ToArray(), observed);
    }

    private void Fail(string stream, TimeSpan bound)
    {
        var elapsedSeconds = _elapsed.Elapsed.TotalSeconds.ToString("0.000", CultureInfo.InvariantCulture);
        var snapshot = _diagnostics.ReadFrozen();
        StopOwnedTree();
        var reason = _stall is null ? "exceeded the retention limit" : $"made no progress for {bound.TotalSeconds:g} s";
        Assert.Fail($"Child {Describe(_startInfo)} {reason} while capturing {stream}; " +
            $"stdout={snapshot.StdoutBytes} bytes; stderr={snapshot.StderrBytes} bytes; " +
            $"retention limit={_retentionBytes} bytes. pid={_pid}; elapsed={elapsedSeconds} s.\n" +
            (_stall is null ? "" : $"{_stall}\n") +
            $"{snapshot.Stdout}\n{snapshot.Stderr}");
    }

    private void StopOwnedTree()
    {
        _diagnostics.Freeze();
        BeginCleanup();
        if (Interlocked.Exchange(ref _stopped, 1) != 0)
            return;
        // Tree termination itself may block. It shares the exit/drain budget;
        // an unfinished stop must not hold up the diagnostic failure.
        var stop = Task.Factory.StartNew(() =>
        {
            try { _stopTree(_process); }
            catch (InvalidOperationException) { } // The child may have exited just before the stop.
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        if (Task.WhenAny(stop).Wait(CleanupRemaining()))
            stop.GetAwaiter().GetResult();
    }

    private void BeginCleanup() =>
        Interlocked.CompareExchange(ref _cleanup, Stopwatch.StartNew(), null);

    private TimeSpan CleanupRemaining()
    {
        var remaining = CleanupMargin - _cleanup!.Elapsed;
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    private bool WaitWithProgress(Task completion, TimeSpan window)
    {
        if (_stall is not null) return false;
        // Credit only slices consumed by the waiter. Injected waiters can advance
        // deterministically, and scheduling delays never manufacture idle time.
        var waited = TimeSpan.Zero;
        var watch = ChildProgressWatch.ForProcess(_process,
            () => Interlocked.Read(ref _stdoutBytes), () => Interlocked.Read(ref _stderrBytes),
            window, () => waited);
        watch.Observe();
        var slice = window < ChildProgressWatch.SamplingSlice ? window : ChildProgressWatch.SamplingSlice;
        while (!completion.IsCompleted)
        {
            if (_wait(completion, slice) || completion.IsCompleted) return true;
            waited += slice;
            _stall = watch.Observe();
            if (_stall is not null) return false;
        }
        return true;
    }

    private static string Decode(byte[] bytes, System.Text.Encoding encoding)
    {
        using var stream = new MemoryStream(bytes);
        using var reader = new StreamReader(stream, encoding, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private static string Describe(ProcessStartInfo startInfo) =>
        $"'{startInfo.FileName}' " +
        (startInfo.ArgumentList.Count > 0
            ? string.Join(" ", startInfo.ArgumentList.Select(QuoteArgument))
            : startInfo.Arguments);

    private static string QuoteArgument(string argument) =>
        argument.Length == 0 || argument.Any(char.IsWhiteSpace) || argument.Contains('"')
            ? "\"" + argument.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\""
            : argument;

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        BeginCleanup();
        if (!_process.HasExited)
            StopOwnedTree();
        try { Task.WhenAll(_exit, _stdout, _stderr).Wait(CleanupRemaining()); }
        catch (AggregateException) { } // Preserve the failure that caused disposal.
        _process.Dispose();
    }
}
