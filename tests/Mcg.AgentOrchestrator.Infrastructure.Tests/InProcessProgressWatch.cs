using System.Globalization;

// Observes host-wide progress; the caller owns pacing and the awaited task.
internal sealed class InProcessProgressWatch
{
    internal static readonly TimeSpan SamplingSlice = TimeSpan.FromSeconds(1);
    internal sealed record Sample(TimeSpan ProcessorTime, long CompletedWorkItems);
    internal sealed record Stall(TimeSpan Window, TimeSpan SinceLastProgress, Sample LastObserved)
    {
        public override string ToString() => string.Create(CultureInfo.InvariantCulture,
            $"made no progress for {Window.TotalSeconds:g} s; processor time={LastObserved.ProcessorTime.TotalSeconds:0.000} s; completed work items={LastObserved.CompletedWorkItems}; time since last progress={SinceLastProgress.TotalSeconds:0.000} s");
    }

    private readonly Func<Sample> _sample;
    private readonly Func<TimeSpan> _now;
    private readonly TimeSpan _window;
    private Sample? _previous;
    private TimeSpan _lastProgress;
    private TimeSpan _lastObservation;

    internal InProcessProgressWatch(Func<Sample> sample, TimeSpan window, Func<TimeSpan> now)
    {
        if (window <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(window));
        _sample = sample;
        _window = window;
        _now = now;
    }

    internal TimeSpan NextSlice => TimeSpan.FromTicks(Math.Min(SamplingSlice.Ticks,
        Math.Max(0, (_window - (_lastObservation - _lastProgress)).Ticks)));

    internal Stall? Observe()
    {
        var current = _sample();
        var now = _now();
        if (_previous is null || current != _previous)
            _lastProgress = now;
        _previous = current;
        _lastObservation = now;
        return now - _lastProgress >= _window
            ? new Stall(_window, now - _lastProgress, current) : null;
    }

    internal static Sample ReadHost() =>
        new(Environment.CpuUsage.TotalTime, ThreadPool.CompletedWorkItemCount);
}
