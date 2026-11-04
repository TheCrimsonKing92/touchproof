using System.Text.Json.Serialization;

namespace Mcg.AgentOrchestrator.Infrastructure;

// Seconds and offsets are in the named observer's clock domain, never remote UTC.
public sealed record AcceptanceLaneWaitMeasurement(
    string Stage, string Boundary, string Source,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? Seconds,
    string? ClockDomain = null, double? StartSeconds = null, double? EndSeconds = null);

public sealed record AcceptanceLaneWaitOccurrence(
    string AttemptId, string OccurrenceId, string Lane, string Route, int Retry,
    string? InvocationId, IReadOnlyList<string> ApplicableStages,
    IReadOnlyList<AcceptanceLaneWaitMeasurement> Measurements);

// Diagnostics have no authority over scheduling. In particular a broken clock/sink leaves unknown
// evidence, and cannot replace a cancellation, execution result, or resource-release exception.
internal sealed class AcceptanceLaneWaitCollector(string attemptId, TimeProvider clock)
{
    private readonly object _sync = new();
    private readonly List<Occurrence> _occurrences = [];
    private readonly string _domain = Guid.NewGuid().ToString("N");
    private long? _origin;

    internal Occurrence Begin(string lane, string route = "local")
    {
        lock (_sync)
        {
            var value = new Occurrence(this, lane, route,
                _occurrences.Count(o => o.Lane == lane && o.Route == route));
            _occurrences.Add(value);
            return value;
        }
    }

    internal void DiscardUnselected(Occurrence occurrence)
    {
        lock (_sync) if (occurrence.Invocation is null) _occurrences.Remove(occurrence);
    }
    internal void Stop(string endpoint)
    {
        lock (_sync) foreach (var occurrence in _occurrences) occurrence.Stop(endpoint);
    }

    internal Occurrence BindLocal(string lane, AcceptanceInvocationContext invocation)
    {
        lock (_sync)
        {
            var value = _occurrences.LastOrDefault(o => o.Lane == lane && o.Route == "local" && o.Invocation is null)
                ?? Begin(lane);
            value.Invocation = invocation;
            return value;
        }
    }

    internal IReadOnlyList<AcceptanceLaneWaitOccurrence> Snapshot()
    {
        lock (_sync)
            return _occurrences.Select(o => new AcceptanceLaneWaitOccurrence(attemptId, o.Id, o.Lane,
                o.Route, o.Invocation?.Ordinal ?? o.Retry, o.Invocation?.InvocationId,
                o.Stages.Order(StringComparer.Ordinal).ToArray(), o.Measurements.Values.ToArray())).ToArray();
    }

    private double? Now()
    {
        var stamp = clock.GetTimestamp();
        _origin ??= stamp;
        var seconds = clock.GetElapsedTime(_origin.Value, stamp).TotalSeconds;
        return double.IsFinite(seconds) && seconds >= 0 ? seconds : null;
    }

    internal sealed class Occurrence(AcceptanceLaneWaitCollector owner, string lane, string route, int retry)
    {
        internal string Id { get; } = Guid.NewGuid().ToString("N");
        internal string Lane { get; } = lane;
        internal string Route { get; } = route;
        internal int Retry { get; } = retry;
        internal AcceptanceInvocationContext? Invocation { get; set; }
        internal HashSet<string> Stages { get; } = ["ready-to-admitted", "admitted-to-execution"];
        internal Dictionary<string, AcceptanceLaneWaitMeasurement> Measurements { get; } = new(StringComparer.Ordinal);
        private readonly Dictionary<string, double?> _starts = new(StringComparer.Ordinal);
        private double? _ready, _admitted;

        private void Observe(Action action)
        {
            try { lock (owner._sync) action(); }
            catch (Exception) { /* Advisory: unavailable evidence stays unknown. */ }
        }

        internal void Ready() => Observe(() => _ready ??= owner.Now());
        internal void Expect(string stage) => Observe(() => Stages.Add(stage));
        internal void Start(string stage) => Observe(() =>
        {
            Stages.Add(stage);
            _starts.TryAdd(stage, owner.Now());
        });
        internal void End(string stage, string boundary) => Observe(() =>
        {
            if (_starts.Remove(stage, out var start)) Add(stage, boundary, start, owner.Now());
        });
        internal void Admit() => Observe(() =>
        {
            if (_admitted is not null) return;
            _admitted = owner.Now();
            Add("ready-to-admitted", "all-local-grants", _ready, _admitted);
        });
        internal void Execute() => Observe(() => Add("admitted-to-execution",
            Route == "remote" ? "submission-start" : "executor-invoked", _admitted, owner.Now()));
        internal void Stop(string endpoint) => Observe(() =>
        {
            foreach (var stage in _starts.Keys.ToArray()) End(stage, endpoint);
        });
        internal void RemotePhases(RemoteLanePhaseTimings? phases) => Observe(() =>
        {
            if (phases is null) return;
            // Reuse the existing computation. Cross-clock differences stay raw in the health ledger;
            // they are not promoted to elapsed samples here.
            Reuse("remote-submit", phases.SubmitSeconds, "submission-steps-local-utc");
            Reuse("remote-queue", null, "unsynchronized-clocks");
            Reuse("remote-runner", phases.RunnerSeconds, "remote-status-duration");
            Reuse("remote-worktree", phases.WorktreeSeconds, "remote-status-duration");
            Reuse("remote-build-wait", phases.BuildWaitSeconds, "remote-status-duration");
            Reuse("remote-build", phases.BuildSeconds, "remote-status-duration");
            Reuse("remote-test", phases.TestSeconds, "remote-trx-clock");
            Reuse("remote-collect", null, "unsynchronized-clocks");
        });
        private void Reuse(string stage, double? seconds, string source)
        {
            Stages.Add(stage);
            Measurements[stage] = new(stage, "existing-remote-phase", source,
                seconds is >= 0 && double.IsFinite(seconds.Value) ? seconds : null);
        }
        private void Add(string stage, string boundary, double? start, double? end)
        {
            if (Measurements.ContainsKey(stage)) return;
            var seconds = end - start;
            Measurements[stage] = new(stage, boundary, "local-monotonic",
                seconds is >= 0 && double.IsFinite(seconds.Value) ? seconds : null,
                owner._domain, start, end);
        }
    }
}
