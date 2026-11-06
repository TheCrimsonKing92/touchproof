namespace Mcg.AgentOrchestrator.App.Orchestration;

// Invocation-owned monotonic observations. Total contains the other, non-overlapping phase spans.
internal sealed class DeveloperCompletionStructuralPhaseTimings(TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly Dictionary<string, TimeSpan> _elapsed = new(StringComparer.Ordinal);
    internal IReadOnlyDictionary<string, TimeSpan> Elapsed => _elapsed;

    internal T Measure<T>(string phase, Func<T> action)
    {
        var started = _time.GetTimestamp();
        try { return action(); }
        finally { _elapsed.Add(phase, _time.GetElapsedTime(started)); }
    }
}
