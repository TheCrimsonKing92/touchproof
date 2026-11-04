using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;

// Invocation-local diagnostics. No observer is retained by the shared template cache.
internal sealed class MergeTrainFixtureObservation
{
    internal static readonly string[] StageNames = ["template-access", "template-lock", "cold-create",
        "copy", "files", "backlinks", "index-refresh", "copy-cleanup", "builder-cleanup",
        "cleanup-context", "kernel", "scenario", "driver", "runner-setup", "projection", "workflow", "receipts",
        "body", "workspace-validation", "cleanup"];
    private readonly Action<string> sink;
    private readonly TimeProvider clock;
    private readonly Func<int>? probes;
    private readonly Dictionary<string, Stage> stages = new(StringComparer.Ordinal);
    private readonly Stack<string> parents = new();
    private bool emitted;
    private bool diagnosticsFailed;
    internal string Id { get; } = Guid.NewGuid().ToString("N");
    internal string? ParentId { get; }
    internal string TemplateState { get; set; } = "not-reached";
    private readonly string mode;

    internal MergeTrainFixtureObservation(string mode, Action<string> sink, TimeProvider? clock = null,
        Func<int>? probes = null, string? parentId = null)
    {
        this.mode = mode.Length > 64 ? mode[..64] : mode;
        this.sink = sink;
        this.clock = clock ?? TimeProvider.System;
        this.probes = probes;
        ParentId = parentId;
    }

    private long? Timestamp()
    {
        try { return clock.GetTimestamp(); }
        catch { diagnosticsFailed = true; return null; }
    }

    private int? ProbeCount()
    {
        try { return probes?.Invoke(); }
        catch { diagnosticsFailed = true; return null; }
    }

    private double? Elapsed(long? start, long? end)
    {
        try
        {
            if (start is null || end is null || end < start) return null;
            var elapsed = clock.GetElapsedTime(start.Value, end.Value).TotalMilliseconds;
            return double.IsFinite(elapsed) && elapsed >= 0 ? elapsed : null;
        }
        catch { diagnosticsFailed = true; return null; }
    }

    internal static void Run(MergeTrainFixtureObservation? observer, string stage, Action action) =>
        Run(observer, stage, () => { action(); return true; });

    internal static T Run<T>(MergeTrainFixtureObservation? observer, string stage, Func<T> action)
    {
        if (observer is null) return action();
        return observer.Measure(stage, action);
    }

    private T Measure<T>(string name, Func<T> action)
    {
        // The vocabulary bounds output independently of operation/assertion counts.
        if (!StageNames.Contains(name, StringComparer.Ordinal)) return action();
        var parent = parents.TryPeek(out var current) ? current : "occurrence";
        var start = Timestamp();
        var count = ProbeCount();
        parents.Push(name);
        var outcome = "passed";
        try { return action(); }
        catch { outcome = "failed"; throw; }
        finally
        {
            parents.Pop();
            var elapsed = Elapsed(start, Timestamp());
            var endCount = ProbeCount();
            var launches = count.HasValue && endCount >= count ? endCount - count : null;
            if (!stages.TryGetValue(name, out var record))
                stages.Add(name, record = new Stage(name, parent));
            record.Count++;
            record.Status = outcome == "failed" || record.Status == "failed" ? "failed" : "passed";
            record.ElapsedMs = record.Count == 1 ? elapsed : Add(record.ElapsedMs, elapsed);
            record.ProbeLaunches = record.Count == 1 ? launches : Add(record.ProbeLaunches, launches);
        }
    }

    private static double? Add(double? left, double? right) =>
        left.HasValue && right.HasValue ? left + right : null;
    private static int? Add(int? left, int? right) =>
        left.HasValue && right.HasValue ? left + right : null;

    // This is also the real helper's lifecycle seam: cleanup retains normal finally precedence.
    internal static void Lifecycle(MergeTrainFixtureObservation? observer, Action body, Action cleanup)
    {
        try { body(); }
        finally { Run(observer, "cleanup", cleanup); }
    }

    internal static void Occurrence(MergeTrainFixtureObservation? observer, Action action)
    {
        if (observer is null) { action(); return; }
        var start = observer.Timestamp();
        var outcome = "passed";
        try { action(); }
        catch { outcome = "failed"; throw; }
        finally { observer.Emit(start, outcome); }
    }

    private void Emit(long? start, string outcome)
    {
        if (emitted) return;
        emitted = true;
        try
        {
            var elapsed = Elapsed(start, Timestamp());
            var top = stages.Values.Where(stage => stage.Parent == "occurrence").ToArray();
            double? accounted = top.All(stage => stage.ElapsedMs.HasValue)
                ? top.Sum(stage => stage.ElapsedMs!.Value) : null;
            var residual = elapsed.HasValue && accounted.HasValue && elapsed >= accounted
                ? elapsed - accounted : null;
            var summary = JsonSerializer.Serialize(new
            {
                kind = "merge-train-fixture", occurrence = Id, parentOccurrence = ParentId, mode,
                template = TemplateState, units = "elapsed-ms", elapsedMs = elapsed,
                accountedMs = accounted, unaccountedMs = residual, outcome,
                accountingScope = "this occurrence; child occurrences overlap the parent body and are not additive",
                cleanup = stages.GetValueOrDefault("cleanup")?.Status ?? "not-reached",
                diagnosticsFailed, fixtureSlotWait = "outside-occurrence",
                waits = "template-lock is directly measured acquisition including overhead; no inferred waits",
                gitCoverage = "fixture probe launches only; null means unavailable; nested records excluded from totals",
                nestedOccurrence = ParentId is not null,
                stages = StageNames.Select(name => stages.TryGetValue(name, out var stage)
                    ? stage : new Stage(name, "not-reached"))
            });
            // Fixed vocabulary and bounded identity fields keep valid JSON below this ceiling.
            if (Encoding.UTF8.GetByteCount(summary) <= 8192) sink(summary);
        }
        catch { /* Reporting must never change a test verdict or interrupt cleanup. */ }
    }

    internal sealed class Stage(string name, string parent)
    {
        public string Name { get; } = name;
        public string Parent { get; } = parent;
        public bool Additive => Parent == "occurrence";
        public int Count { get; set; }
        public string Status { get; set; } = "not-reached";
        public double? ElapsedMs { get; set; }
        public int? ProbeLaunches { get; set; }
        public string Measurement => Count == 0 ? "not-reached" : ElapsedMs.HasValue ? "observed" : "unavailable";
    }

    // Like Lazy ExecutionAndPublication: independent entries, one build, cached failures,
    // recursive initialization rejected. The observer exists only on the Get call stack.
    internal sealed class Initialization<T>(Func<MergeTrainFixtureObservation?, T> factory)
    {
        private readonly object sync = new();
        private bool initialized;
        private bool initializing;
        private T? value;
        private ExceptionDispatchInfo? failure;

        internal T Get(MergeTrainFixtureObservation? observer)
        {
            var entered = false;
            try
            {
                Run(observer, "template-lock", () => Monitor.Enter(sync, ref entered));
                if (initializing) throw new InvalidOperationException("Recursive template initialization.");
                if (!initialized)
                {
                    if (observer is not null) observer.TemplateState = "cold";
                    initializing = true;
                    try { value = Run(observer, "cold-create", () => factory(observer)); }
                    catch (Exception ex) { failure = ExceptionDispatchInfo.Capture(ex); }
                    finally { initializing = false; initialized = true; }
                }
                else if (observer is not null) observer.TemplateState = failure is null ? "warm" : "cached-failure";
                failure?.Throw();
                return value!;
            }
            finally { if (entered) Monitor.Exit(sync); }
        }
    }
}
