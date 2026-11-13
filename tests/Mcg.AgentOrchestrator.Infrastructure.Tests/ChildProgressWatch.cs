using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;

// Observes one child's progress; the caller owns waiting, termination and output drains.
internal sealed class ChildProgressWatch
{
    internal static readonly TimeSpan SamplingSlice = TimeSpan.FromSeconds(1);
    internal sealed record Sample(bool Alive, TimeSpan? ProcessorTime, long StdoutBytes, long StderrBytes);
    internal sealed record Stall(TimeSpan Window, TimeSpan SinceLastProgress, Sample LastObserved)
    {
        public override string ToString() => string.Create(CultureInfo.InvariantCulture,
            $"made no progress for {Window.TotalSeconds:g} s; processor time={LastObserved.ProcessorTime?.TotalSeconds.ToString("0.000", CultureInfo.InvariantCulture) ?? "unknown"} s; stdout={LastObserved.StdoutBytes} bytes; stderr={LastObserved.StderrBytes} bytes; time since last progress={SinceLastProgress.TotalSeconds:0.000} s");
    }

    private readonly Func<Sample> _sample;
    private readonly Func<TimeSpan> _now;
    private readonly TimeSpan _window;
    private Sample? _previous;
    private TimeSpan _lastProgress;

    internal ChildProgressWatch(Func<Sample> sample, TimeSpan window, Func<TimeSpan> now)
    {
        if (window <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(window));
        _sample = sample;
        _window = window;
        _now = now;
    }

    internal Stall? Observe()
    {
        var current = _sample();
        var now = _now();
        if (_previous is null || !_previous.Alive || !current.Alive || current.ProcessorTime is null ||
            current.ProcessorTime != _previous.ProcessorTime ||
            current.StdoutBytes != _previous.StdoutBytes || current.StderrBytes != _previous.StderrBytes)
            _lastProgress = now;
        _previous = current;
        return current.Alive && now - _lastProgress >= _window
            ? new Stall(_window, now - _lastProgress, current) : null;
    }

    internal static ChildProgressWatch ForProcess(Process process, Func<long> stdoutBytes,
        Func<long> stderrBytes, TimeSpan window, Func<TimeSpan> now) =>
        new(() => ReadProcess(process, stdoutBytes(), stderrBytes()), window, now);

    private static Sample ReadProcess(Process process, long stdoutBytes, long stderrBytes)
    {
        TimeSpan? cpu = null;
        try
        {
            process.Refresh();
            if (process.HasExited) return new(false, null, stdoutBytes, stderrBytes);
            cpu = process.TotalProcessorTime;
        }
        catch (InvalidOperationException) when (process.HasExited)
        {
            return new(false, null, stdoutBytes, stderrBytes);
        }
        catch (Win32Exception)
        {
            // An unreadable CPU sample cannot establish that a live child is idle.
        }
        return new(!process.HasExited, cpu, stdoutBytes, stderrBytes);
    }
}
