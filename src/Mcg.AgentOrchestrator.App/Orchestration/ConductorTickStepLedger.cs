using System.Globalization;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// One tick owns this telemetry. Ambient scope reaches synchronous CLI/store seams,
// but is restored on every exit and cannot keep recording after the tick ends.
internal sealed class ConductorTickStepLedger
{
    internal static readonly IReadOnlyList<string> OperatorIntentNames = Array.AsReadOnly(new[]
    {
        "operator-intent-list"
    });
    internal static readonly IReadOnlyList<string> PrewalkNames = Array.AsReadOnly(new[]
    {
        "retire-until-goal-lessons", "workspace-intents", "steward", "author", "store-evidence",
        "judge-panel", "board-fill", "eligibility"
    });
    internal static readonly IReadOnlyList<string> SweepNames = Array.AsReadOnly(new[]
    {
        "kernel-reload", "refresh-dispatches", "parked-reloads", "terminal-sweep-run", "owned-root-reap",
        "reconcile-remediation", "host-health", "maintenance", "state-log-check", "failure-clusters",
        "remote-git-mirror", "goal-refinement", "program-health"
    });
    internal static readonly IReadOnlyList<string> AdmissionNames = Array.AsReadOnly(new[]
    {
        "attempt-census", "attempt-reconcile", "grouped-admission-prep", "candidate-supersession", "merge-train", "pair-cohort", "solo-admission",
        "activation-probe-record", "priority-order", "hold-cohort-members", "hold-lone-ready",
        "priority-guard", "deferral-summary", "admission-pre-batch", "begin-tick"
    });
    internal static readonly IReadOnlyList<string> PrepNames = Array.AsReadOnly(new[]
    {
        "prep-cohort-receipts", "prep-train-reconcile", "prep-projection", "prep-census", "prep-plan-receipt",
        "prep-cohort-members", "prep-obligation-filter", "prep-merge-conflict", "prep-approval-hold",
        "prep-train-receipts", "prep-oldest-waiter", "prep-retry-priority",
        "prep-interaction-keys", "prep-probe-backoff"
    });
    private static readonly AsyncLocal<ConductorTickStepLedger?> Current = new();
    private readonly Func<TimeSpan> _cpu;
    private readonly Action<string>? _probe;
    private readonly int _ownerThread = Environment.CurrentManagedThreadId;
    private readonly object _gate = new();
    private readonly Dictionary<string, (long Ticks, long WallTicks, long Calls, long InclusiveTicks, long InclusiveWallTicks)> _steps = new(StringComparer.Ordinal);
    private readonly Stack<Frame> _frames = new();
    private bool _active;
    private long _goalsHydrated, _metadataRows, _terminalJournalStats, _backlogRows, _readinessEvaluations;
    private long _projectionCount, _slowestProjectionTicks, _projectionCacheHits, _projectionCacheMisses;
    private long _mergeConflictCacheHits, _mergeConflictCacheMisses, _approvalHoldCacheHits, _approvalHoldCacheMisses;
    private string? _slowestProjectionGoalId;
    private readonly HashSet<GoalId> _factsReady = [], _factsPending = [];
    private long _oldestFactsPendingMs;

    internal static void CountAdmissionFacts(GoalId id, bool ready, TimeSpan age) => AddCounter(ledger =>
    {
        if (ready) ledger._factsReady.Add(id);
        else
        {
            ledger._factsPending.Add(id);
            ledger._oldestFactsPendingMs = Math.Max(ledger._oldestFactsPendingMs, (long)Math.Max(0, age.TotalMilliseconds));
        }
    });

    internal ConductorTickStepLedger(Func<TimeSpan> cpu, Action<string>? probe = null)
    {
        _cpu = cpu;
        _probe = probe;
    }

    internal IDisposable Activate()
    {
        var previous = Current.Value;
        lock (_gate) _active = true;
        Current.Value = this;
        return new Lifetime(this, previous);
    }

    internal static T Measure<T>(string name, Func<T> action)
    {
        var ledger = Current.Value;
        if (ledger is null) return action();
        Frame? frame = null;
        bool active;
        lock (ledger._gate)
        {
            active = ledger._active;
            if (active)
            {
                var old = ledger._steps.GetValueOrDefault(name);
                ledger._steps[name] = (old.Ticks, old.WallTicks, old.Calls + 1, old.InclusiveTicks, old.InclusiveWallTicks);
                // Other threads still count calls; their process CPU overlaps the owner's
                // scopes and must not be added to this partition of the phase.
                frame = Environment.CurrentManagedThreadId == ledger._ownerThread
                    ? new Frame(ledger._cpu()) : null;
                if (frame is not null) ledger._frames.Push(frame);
            }
        }
        if (!active) return action();
        try
        {
            var result = action();
            ledger._probe?.Invoke(name);
            return result;
        }
        finally
        {
            if (frame is not null)
            {
                lock (ledger._gate)
                {
                    var elapsed = Math.Max(0, (ledger._cpu() - frame.Start).Ticks);
                    var wallElapsed = frame.Wall.Elapsed.Ticks;
                    ledger._frames.Pop();
                    if (ledger._frames.TryPeek(out var parent))
                    {
                        parent.ChildTicks += elapsed;
                        parent.ChildWallTicks += wallElapsed;
                    }
                    var old = ledger._steps[name];
                    ledger._steps[name] = (old.Ticks + elapsed - frame.ChildTicks,
                        old.WallTicks + Math.Max(0, wallElapsed - frame.ChildWallTicks), old.Calls,
                        old.InclusiveTicks + elapsed, old.InclusiveWallTicks + wallElapsed);
                }
            }
        }
    }

    internal static void Measure(string name, Action action) => Measure(name, () => { action(); return true; });

    // ProjectAll projects each yielded goal synchronously before asking for the next.
    // Observing that interval keeps projection execution and its ordering untouched.
    internal static IEnumerable<Goal> ObserveProjections(IEnumerable<Goal> goals)
    {
        var ledger = Current.Value;
        return ledger is null ? goals : ledger.ObserveProjectionIntervals(goals);
    }

    private IEnumerable<Goal> ObserveProjectionIntervals(IEnumerable<Goal> goals)
    {
        foreach (var goal in goals)
        {
            var timer = ConductorBatchLoop.StartDiagnosticTimer();
            try { yield return goal; }
            finally
            {
                var ticks = timer.Elapsed.Ticks;
                lock (_gate)
                {
                    if (_active)
                    {
                        _projectionCount++;
                        if (Environment.CurrentManagedThreadId == _ownerThread &&
                            (_slowestProjectionGoalId is null || ticks > _slowestProjectionTicks ||
                             (ticks == _slowestProjectionTicks && string.CompareOrdinal(goal.Id.Value, _slowestProjectionGoalId) < 0)))
                        {
                            _slowestProjectionGoalId = goal.Id.Value;
                            _slowestProjectionTicks = ticks;
                        }
                    }
                }
            }
        }
    }

    internal static IReadOnlyList<T> CountMetadataRows<T>(IReadOnlyList<T> rows)
    {
        AddCounter(ledger => Interlocked.Add(ref ledger._metadataRows, rows.Count));
        return rows;
    }

    internal static AgentOrchestratorKernel CountGoalsHydrated(AgentOrchestratorKernel kernel)
    {
        AddCounter(ledger => Interlocked.Add(ref ledger._goalsHydrated, kernel.Goals.Count));
        return kernel;
    }

    internal static T CountTerminalJournalCheck<T>(Func<T> read)
    {
        AddCounter(ledger => Interlocked.Increment(ref ledger._terminalJournalStats));
        return read();
    }

    internal static IReadOnlyList<T> CountBacklogRows<T>(IReadOnlyList<T> rows)
    {
        AddCounter(ledger => Interlocked.Add(ref ledger._backlogRows, rows.Count));
        return rows;
    }

    internal static Func<BacklogItem, BacklogReadiness> CountReadinessEvaluations(Func<BacklogItem, BacklogReadiness> evaluate)
    {
        // Capture this tick, rather than resolving a later tick at evaluation time.
        var ledger = Current.Value;
        if (ledger is null) return evaluate;
        return item =>
        {
            lock (ledger._gate)
                if (ledger._active) Interlocked.Increment(ref ledger._readinessEvaluations);
            return evaluate(item);
        };
    }

    internal static void CountProjectionCache(bool hit) => AddCounter(ledger =>
    {
        if (hit) ledger._projectionCacheHits++;
        else ledger._projectionCacheMisses++;
    });

    internal static void CountMergeConflictCache(bool hit) => AddCounter(ledger =>
    {
        if (hit) ledger._mergeConflictCacheHits++;
        else ledger._mergeConflictCacheMisses++;
    });

    internal static void CountApprovalHoldCache(bool hit) => AddCounter(ledger =>
    {
        if (hit) ledger._approvalHoldCacheHits++;
        else ledger._approvalHoldCacheMisses++;
    });

    private static void AddCounter(Action<ConductorTickStepLedger> increment)
    {
        var ledger = Current.Value;
        if (ledger is null) return;
        lock (ledger._gate)
            if (ledger._active) increment(ledger);
    }

    internal string FormatSteps(IReadOnlyList<string> names)
    {
        lock (_gate)
            return string.Join(',', names.Select(name =>
            {
                var value = _steps.GetValueOrDefault(name);
                return FormattableString.Invariant($"{name}:{value.Ticks / TimeSpan.TicksPerMillisecond}:{value.Calls}");
            }));
    }

    internal string FormatAdmission(TimeSpan elapsed)
    {
        lock (_gate)
        {
            long attributedMs = 0;
            var steps = string.Join(',', AdmissionNames.Select(name =>
            {
                var value = _steps.GetValueOrDefault(name);
                // Prep children are reported separately, so the parent remains inclusive
                // here without adding those child durations to the phase a second time.
                var wallMs = (name == "grouped-admission-prep" ? value.InclusiveWallTicks : value.WallTicks) / TimeSpan.TicksPerMillisecond;
                var cpuTicks = name == "grouped-admission-prep" ? value.InclusiveTicks : value.Ticks;
                attributedMs += wallMs;
                return FormattableString.Invariant($"{name}:{wallMs}:{cpuTicks / TimeSpan.TicksPerMillisecond}:{value.Calls}");
            }));
            var remainder = Math.Max(0, elapsed.Ticks / TimeSpan.TicksPerMillisecond - attributedMs);
            long prepWallTicks = 0;
            var prepSteps = string.Join(',', PrepNames.Select(name =>
            {
                var value = _steps.GetValueOrDefault(name);
                prepWallTicks += value.InclusiveWallTicks;
                return FormattableString.Invariant($"{name}:{value.InclusiveWallTicks / TimeSpan.TicksPerMillisecond}:{value.InclusiveTicks / TimeSpan.TicksPerMillisecond}:{value.Calls}");
            }));
            var prepRemainder = Math.Max(0, _steps.GetValueOrDefault("grouped-admission-prep").InclusiveWallTicks - prepWallTicks) / TimeSpan.TicksPerMillisecond;
            var slowest = _slowestProjectionGoalId is null ? "none:0" :
                FormattableString.Invariant($"{_slowestProjectionGoalId[..8]}:{_slowestProjectionTicks / TimeSpan.TicksPerMillisecond}");
            var substeps = string.Join(',', new[] { "projection-inputs", "projection-inputs-frozen", "projection-inputs-refs", "projection-inputs-approvals", "projection-inputs-key", "projection-facts-lookup", "projection-policy", "projection-key", "projection-key-refs", "projection-key-snapshot", "projection-cache", "projection-owner-approval", "approval-inputs", "approval-projections", "approval-facts-lookup", "approval-receipts" }.Select(name =>
            {
                var value = _steps.GetValueOrDefault(name);
                return FormattableString.Invariant($"{name}:{value.InclusiveWallTicks / TimeSpan.TicksPerMillisecond}:{value.InclusiveTicks / TimeSpan.TicksPerMillisecond}:{value.Calls}");
            }));
            return FormattableString.Invariant($"admission_steps={steps} unattributed_ms={remainder} prep_steps={prepSteps} prep_projection_count={_projectionCount} prep_projection_slowest={slowest} prep_unattributed_ms={prepRemainder} prep_projection_cache=hits:{_projectionCacheHits},misses:{_projectionCacheMisses} prep_merge_conflict_cache=hits:{_mergeConflictCacheHits},misses:{_mergeConflictCacheMisses} prep_approval_hold_cache=hits:{_approvalHoldCacheHits},misses:{_approvalHoldCacheMisses} prep_substeps={substeps} admission_facts=ready:{_factsReady.Count},pending:{_factsPending.Count},oldest_pending_ms:{_oldestFactsPendingMs}");
        }
    }

    internal string FormatCounters() => string.Join(',', new[]
    {
        ("goals_hydrated", Interlocked.Read(ref _goalsHydrated)),
        ("metadata_rows", Interlocked.Read(ref _metadataRows)),
        ("terminal_journal_stats", Interlocked.Read(ref _terminalJournalStats)),
        ("backlog_rows_read", Interlocked.Read(ref _backlogRows)),
        ("readiness_evaluations", Interlocked.Read(ref _readinessEvaluations))
    }.Select(pair => pair.Item1 + "=" + pair.Item2.ToString(CultureInfo.InvariantCulture)));

    private sealed class Frame(TimeSpan start)
    {
        internal TimeSpan Start { get; } = start;
        internal ConductorBatchLoop.DiagnosticTimer Wall { get; } = ConductorBatchLoop.StartDiagnosticTimer();
        internal long ChildTicks { get; set; }
        internal long ChildWallTicks { get; set; }
    }

    private sealed class Lifetime(ConductorTickStepLedger ledger, ConductorTickStepLedger? previous) : IDisposable
    {
        public void Dispose()
        {
            lock (ledger._gate) ledger._active = false;
            Current.Value = previous;
        }
    }
}
