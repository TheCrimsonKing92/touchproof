using System.Text;
using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;

// One instance describes one fixture occurrence. This never installs a process-wide observer.
internal sealed class BrokerDeliveryFixtureObservation
{
    internal const string Prefix = "BROKER_DELIVERY ";
    internal const int MaximumSummaryBytes = 4096;
    internal enum Phase { Repository, Scenario, PrepareTask, Assertions, Cleanup, CleanupRetryWait, FixtureSlotWait }
    private static readonly string[] Names =
        ["repository", "scenario", "prepare-task", "assertions", "cleanup", "cleanup-retry-wait", "fixture-slot-wait"];

    private readonly Action<string> sink;
    private readonly TimeProvider clock;
    private readonly Func<object, string> format;
    private readonly Func<Func<int?>?>? openGitWindow;
    private readonly Stage?[] stages = new Stage?[Names.Length];
    private readonly string caseIdentity;
    private readonly string contextIdentity;
    private readonly int useOrdinal;
    private readonly string occurrence = Guid.NewGuid().ToString("N");
    private long? started;
    private double frequency;
    private Stage? current;
    private bool observationFailed;
    private bool finished;
    private string setup = "not-entered";

    internal BrokerDeliveryFixtureObservation(Action<string> sink, string caseIdentity,
        string? contextIdentity = null, int useOrdinal = 1, TimeProvider? clock = null,
        Func<object, string>? format = null, Func<Func<int?>?>? openGitWindow = null)
    {
        this.sink = sink;
        this.clock = clock ?? TimeProvider.System;
        this.format = format ?? (value => JsonSerializer.Serialize(value));
        this.openGitWindow = openGitWindow;
        this.caseIdentity = Bound(caseIdentity, 160);
        this.contextIdentity = Bound(contextIdentity ?? occurrence, 48);
        this.useOrdinal = useOrdinal;
        Guard(() =>
        {
            frequency = this.clock.TimestampFrequency;
            if (frequency <= 0 || useOrdinal < 1) throw new InvalidOperationException("Invalid observation lifecycle/clock.");
            started = this.clock.GetTimestamp();
        });
    }

    // The same wrapper is used by real cases and deterministic failure controls. In particular,
    // cleanup retains ordinary finally precedence over an escaping assertion exception.
    internal static void Execute<T>(BrokerDeliveryFixtureObservation observation,
        Func<T> prepare, Action<T> body, Action<T> cleanup)
    {
        try
        {
            T fixture;
            try { fixture = prepare(); observation.setup = "completed"; }
            catch { observation.setup = "failed"; throw; }
            try { observation.Measure(Phase.Assertions, () => body(fixture)); }
            finally { cleanup(fixture); }
        }
        finally { observation.Complete(); }
    }

    internal void Measure(Phase phase, Action action) => Measure(phase, () => { action(); return 0; });

    internal T Measure<T>(Phase phase, Func<T> action)
    {
        Stage? stage = null;
        Func<int?>? closeGitWindow = null;
        var parent = current;
        Guard(() =>
        {
            if (finished) throw new InvalidOperationException("Observation already finalized.");
            var index = (int)phase;
            stage = stages[index];
            if (stage is not null && phase != Phase.CleanupRetryWait)
                throw new InvalidOperationException("Phase entered more than once.");
            stage ??= stages[index] = new Stage(phase, parent?.Phase);
            stage.Entries++;
            closeGitWindow = openGitWindow?.Invoke();
            stage.Start = clock.GetTimestamp();
            current = stage;
        });
        var succeeded = false;
        try { var result = action(); succeeded = true; return result; }
        finally
        {
            if (stage is not null) stage.Status = succeeded ? "completed" : "failed";
            var sampled = false;
            Guard(() =>
            {
                if (stage is null) return;
                stage.End = clock.GetTimestamp();
                stage.Elapsed += Milliseconds(stage.Start, stage.End);
                sampled = true;
            });
            if (stage is not null && !sampled) stage.Elapsed = null;
            Guard(() =>
            {
                if (stage is null) return;
                var launches = closeGitWindow?.Invoke();
                stage.GitLaunches = stage.Entries == 1 ? launches :
                    stage.GitLaunches is { } prior && launches is { } next ? checked(prior + next) : null;
            });
            current = parent;
        }
    }

    internal void Complete()
    {
        if (finished) return;
        finished = true; // Sink/format failures never cause retries or duplicate delivery.
        long? end = null;
        Guard(() => end = clock.GetTimestamp());
        Guard(() =>
        {
            var elapsed = Milliseconds(started, end);
            var intervals = stages.Where(stage => stage is { Parent: null, End: not null, Start: not null })
                .Select(stage => (Start: Math.Max(started ?? stage!.Start!.Value, stage!.Start!.Value),
                    End: Math.Min(end ?? stage.End!.Value, stage.End!.Value))).OrderBy(interval => interval.Start).ToArray();
            long covered = 0;
            long? right = null;
            foreach (var interval in intervals)
            {
                if (interval.End <= interval.Start) continue;
                covered += Math.Max(0, interval.End - Math.Max(interval.Start, right ?? interval.Start));
                right = Math.Max(right ?? interval.End, interval.End);
            }
            var summary = new
            {
                schema = 1, occurrence, @case = caseIdentity, context = contextIdentity,
                lifecycle = useOrdinal == 1 ? "cold" : "warm", useOrdinal,
                lifecycleMeaning = "repository-package-context-use", units = "ms", gitUnits = "launches",
                elapsed, unaccounted = elapsed is null ? null : (double?)Math.Max(0, elapsed.Value - covered * 1000d / frequency),
                observation = observationFailed ? "incomplete" : "observed", setup,
                stages = Enumerable.Range(0, (int)Phase.CleanupRetryWait + 1).Select(Describe).ToArray(),
                fixtureSlotWait = Describe((int)Phase.FixtureSlotWait)
            };
            var line = Prefix + format(summary);
            if (Encoding.UTF8.GetByteCount(line) > MaximumSummaryBytes)
                throw new InvalidOperationException("Observation summary exceeded its bound.");
            sink(line);
        });
    }

    private object Describe(int index)
    {
        var stage = stages[index];
        return new
        {
            stage = Names[index], status = stage?.Status ?? (index >= (int)Phase.CleanupRetryWait ? "not-observed" : "not-entered"),
            elapsed = stage?.Elapsed, entries = stage?.Entries ?? 0,
            parent = stage?.Parent is { } parent ? Names[(int)parent] : null,
            additive = stage is not null && stage.Parent is null && index != (int)Phase.FixtureSlotWait,
            kind = index >= (int)Phase.CleanupRetryWait ? "observed-wait" : "elapsed",
            gitLaunches = stage?.GitLaunches,
            gitStatus = stage?.GitLaunches is not null ? "observed" : "unavailable-no-exact-invocation-scope"
        };
    }

    // Opted into only by the two target classes: xUnit serializes their rows within each
    // class, and each row opens one fixture. The existing census keys class + method.
    // Never install/replace its observer or interpret a dormant census as zero launches.
    internal static Func<int?>? OpenRetainedGitWindow()
    {
        Action<string> recorder = HostCapacitySlotGitCensus.Shared.Record;
        if (Volatile.Read(ref GitCli.LaunchObserver) != recorder) return null;
        var name = HostCapacitySlotTimingWriter.CurrentTestName();
        if (name == "<unknown>") return null;
        var window = HostCapacitySlotGitCensus.Shared.Open(name);
        return window is null ? null : () => Volatile.Read(ref GitCli.LaunchObserver) == recorder &&
            HostCapacitySlotTimingWriter.CurrentTestName() == name ? window.Close()?.Launches : null;
    }

    private double? Milliseconds(long? start, long? end) => start is null || end is null || frequency <= 0
        ? null : (end.Value - start.Value) * 1000d / frequency;

    private void Guard(Action action)
    {
        try { action(); }
        catch (Exception) { observationFailed = true; }
    }

    // Restrict metadata before JSON serialization; never truncate serialized JSON.
    private static string Bound(string value, int length) => new(value.Take(length)
        .Select(character => character is >= ' ' and <= '~' && character is not ('"' or '\\') ? character : '_').ToArray());

    private sealed class Stage(Phase phase, Phase? parent)
    {
        internal Phase Phase { get; } = phase;
        internal Phase? Parent { get; } = parent;
        internal long? Start;
        internal long? End;
        internal double? Elapsed = 0;
        internal int Entries;
        internal int? GitLaunches;
        internal string Status = "interrupted";
    }
}
