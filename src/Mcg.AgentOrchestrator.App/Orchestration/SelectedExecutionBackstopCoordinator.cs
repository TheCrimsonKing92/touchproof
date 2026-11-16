using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Mcg.AgentOrchestrator.App.OwnerConsole;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// The conductor owns admission; this coordinator owns one durable request and one local task.
// All state transitions (including landing callbacks) are serialized, but gate execution is not.
internal sealed class SelectedExecutionBackstopCoordinator(
    SelectedExecutionBackstopStore store, ISelectedExecutionBackstopRunner runner,
    ICollaborationItemStore items, string home, Func<DateTimeOffset> clock, Action<string> progress,
    Func<string, string, bool>? isAncestor = null, string? stateHome = null,
    SelectedExecutionBackstopYieldBounds? bounds = null)
{
    private readonly SemaphoreSlim _transitions = new(1, 1);
    private Task? _running;
    private RunningCancellation? _runningCancellation;
    private volatile bool _restartHeld;
    private string? _activeRequestId;
    private bool _initialTickServiced;
    private IReadOnlyDictionary<string, (DateTimeOffset ReadyAt, GoalPriority Level)> _admissionOrder =
        new Dictionary<string, (DateTimeOffset, GoalPriority)>();
    internal int RunningCount => _running is { IsCompleted: false } ? 1 : 0;
    internal string? ActiveRequestId => RunningCount == 0 ? null : _activeRequestId;
    internal SelectedExecutionBackstopYieldBounds Bounds { get; } = bounds ?? SelectedExecutionBackstopYieldBounds.Default;

    internal static SelectedExecutionBackstopCoordinator? Create(OrchestratorWorkspace? workspace,
        ConductorDriver driver, Func<DateTimeOffset> clock, Action<string> progress) =>
        workspace is null || driver._cohortAcceptanceVerifier is null ? null : new(
            new(new SqliteRunEventStore(workspace.RunEventStorePath)), new SelectedExecutionBackstopGateRunner(driver),
            CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory),
            AcceptanceSelectedExecutionSessionSuspension.Home(workspace.ExecutionDirectory, workspace.ProjectHomeDirectoryOrNull),
            clock, progress, (commit, target) => GitCli.Run(workspace.ExecutionDirectory, "merge-base", "--is-ancestor", commit, target).Succeeded,
            workspace.OrchestratorDirectory, SelectedExecutionBackstopYieldBounds.FromSettings(
                AcceptanceGateEngineSettings.Load(workspace.ExecutionDirectory, workspace.ProjectHomeDirectoryOrNull)));

    internal void NoteLanding(ConductorLandingReceipt landing, string worktree)
    {
        var path = landing.GateLanePlanPath;
        if (path is null && landing.GateAttemptId is { } attempt)
        {
            path = Path.Combine(stateHome ?? home, "acceptance-gate-attempts", landing.GoalId, attempt + AcceptanceGateLanePlan.ArtifactSuffix);
            if (!File.Exists(path)) path = Path.Combine(AcceptanceOwnerResultsRoot.ResolveOwnerResultsRootForCandidate(worktree),
                ".orchestrator", "acceptance-gate-attempts", landing.GoalId, attempt + AcceptanceGateLanePlan.ArtifactSuffix);
        }
        var record = path is null ? null : new OwnerConsoleGateLanePlanReader().Read(path,
            landing.GateAttemptId ?? Path.GetFileName(path)[..^AcceptanceGateLanePlan.ArtifactSuffix.Length])?.SelectedExecution;
        // Only the gate's record establishes applied selection. Missing evidence also
        // requires a backstop, with unreadability retained on the landing.
        NoteLandingAsync(landing.GoalId, landing.LandingSha ?? throw new InvalidDataException("Backstop landing has no commit."),
            record, landing.PreviousMainSha ?? GoalWorktrees.ResolveRequiredRef(worktree, landing.LandingSha + "^")).GetAwaiter().GetResult();
    }

    internal async Task NoteLandingAsync(string goalId, string commit, AcceptanceSelectedExecutionRecord? record, string previousMain)
    {
        await _transitions.WaitAsync().ConfigureAwait(false);
        try
        {
            var state = await store.ReadAsync().ConfigureAwait(false) ?? new(previousMain);
            if (await store.HasLandingAsync(goalId, commit).ConfigureAwait(false)) return;
            var ordinal = state.LastLandingOrdinal + 1;
            var recordedAt = clock();
            var applied = record?.Applied == true || record?.StructuralCoverageScoped == true;
            if (record is null)
                progress($"SELECTED_EXECUTION_BACKSTOP result=landing-record-unreadable goal={goalId} commit={commit}");
            var landing = new SelectedExecutionBackstopLanding(ordinal, goalId, commit, applied, record is null)
            {
                SkippedClasses = applied ? (record!.Lanes ?? []).SelectMany(lane => lane.SkippedClasses ?? [])
                    .Distinct(StringComparer.Ordinal).ToArray() : []
            };
            await store.WriteAsync(record is null ? "landing-record-unreadable" : "landing", state with
            { LastLandingOrdinal = ordinal, Landings = state.RangeLandings.Append(landing).ToArray(),
                LandingInstants = state.RecordedLandingInstants.Append(new(ordinal, recordedAt)).ToArray() }, recordedAt,
                SelectedExecutionBackstopStore.LandingId(goalId, commit)).ConfigureAwait(false);
        }
        finally { _transitions.Release(); }
    }

    internal async Task ServiceAsync(Func<string> mainHead, IReadOnlyList<Goal> waiting,
        IReadOnlyCollection<Goal> waitingForAcceptance, bool capacityAvailable, bool suppressed)
    {
        await _transitions.WaitAsync().ConfigureAwait(false);
        try
        {
            var initialTick = !_initialTickServiced;
            _initialTickServiced = true;
            var state = await store.ReadAsync().ConfigureAwait(false);
            if (state is null) return;
            var missingInstants = state.Uncovered.Where(landing => !state.RecordedLandingInstants
                .Any(instant => instant.Ordinal == landing.Ordinal)).ToArray();
            if (missingInstants.Length > 0)
            {
                // Legacy snapshots age from first observation, durably across retries and restarts.
                var observedAt = clock();
                state = state with { LandingInstants = state.RecordedLandingInstants.Concat(missingInstants
                    .Select(landing => new SelectedExecutionBackstopLandingInstant(landing.Ordinal, observedAt))).ToArray() };
                await store.WriteAsync("landing-instants-observed", state, observedAt).ConfigureAwait(false);
            }
            state = await ReconcileMissAsync(state).ConfigureAwait(false);
            _admissionOrder = waiting.ToDictionary(goal => goal.Id.Value,
                goal => (GoalSchedulingOrder.AcceptanceWaitingAt(goal), GoalEffectivePriorityIndex.EffectiveLevel(goal)), StringComparer.Ordinal);
            if (state.OpenMiss is not null || RunningCount != 0 || _restartHeld) return;
            if (state.Request is null && state.Uncovered.Count > 0)
            {
                var request = new SelectedExecutionBackstopRequest(Guid.NewGuid().ToString("N"),
                    state.CoveredCommit ?? state.RangeStartCommit, mainHead(), state.LastLandingOrdinal,
                    state.RangeLandings.Select(landing => landing.GoalId).Distinct(StringComparer.Ordinal).ToArray(), clock());
                state = state with { Request = request };
                await store.WriteAsync("queued", state, clock()).ConfigureAwait(false);
                Report("queued", request, detail: state.Uncovered.Any(landing => landing.RecordUnreadable) ? "record-unreadable" : null,
                    appliedLandings: state.Uncovered);
            }
            // Pending requests cover the newest head; interrupted attempts retain their fixed range.
            if (state.Request is { } queued && (queued.Attempts == 0 || queued.RetryPending) && mainHead() is { } newest &&
                (newest != queued.ToCommit || queued.ThroughOrdinal != state.LastLandingOrdinal))
            {
                state = state with { Request = queued with { ToCommit = newest, ThroughOrdinal = state.LastLandingOrdinal,
                    GoalIds = state.RangeLandings.Select(landing => landing.GoalId).Distinct(StringComparer.Ordinal).ToArray() } };
                await store.WriteAsync("coalesced", state, clock()).ConfigureAwait(false);
            }
            if (state.Request is not { } pending || initialTick || suppressed || !capacityAvailable) return;
            var rule = SelectedExecutionBackstopYieldPolicy.Decide(waitingForAcceptance.Count, state.Uncovered.Count,
                state.OldestUncoveredAt is { } oldest ? clock() - oldest : null, Bounds);
            if (rule is null) return;
            var started = pending with { Attempts = pending.Attempts + 1, RetryPending = false };
            await store.WriteAsync("started", state with { Request = started }, clock()).ConfigureAwait(false);
            Report(pending.Attempts > 0 ? "resumed" : "started", started, rule: rule,
                covered: state.Uncovered.Count(landing => landing.Ordinal <= started.ThroughOrdinal));
            _activeRequestId = started.Id;
            var cancellation = new RunningCancellation(started.Id);
            Volatile.Write(ref _runningCancellation, cancellation);
            _running = Task.Run(async () =>
            {
                try { await ExecuteAsync(started, cancellation.Token).ConfigureAwait(false); }
                catch (Exception ex)
                {
                    // State or notification I/O is retried from the durable request/miss on
                    // the next service tick. It must remain distinct from a gate verdict.
                    try { Report("state-error", started, detail: $"{ex.GetType().Name}: {ex.Message}"); }
                    catch (Exception) { /* A progress sink cannot strand a lifecycle drain. */ }
                }
                finally
                {
                    Interlocked.CompareExchange(ref _runningCancellation, null, cancellation);
                    cancellation.Complete();
                }
            });
        }
        finally { _transitions.Release(); }
    }

    internal async Task ObserveServedAsync(IEnumerable<Goal> served)
    {
        await _transitions.WaitAsync().ConfigureAwait(false);
        try
        {
            var state = await store.ReadAsync().ConfigureAwait(false);
            if (RunningCount != 0 || state?.Request is not { } request) return;
            var added = served.DistinctBy(goal => goal.Id.Value).Count(goal => _admissionOrder.TryGetValue(goal.Id.Value, out var key) &&
                key.ReadyAt > request.QueuedAt && key.Level > GoalEffectivePriorityIndex.EffectiveLevel(GoalPriority.Normal, request.Overtakes));
            if (added > 0) await store.WriteAsync("overtaken", state with { Request = request with
            { Overtakes = request.Overtakes + added } }, clock()).ConfigureAwait(false);
        }
        finally { _transitions.Release(); }
    }

    internal Task DrainAsync() => _running ?? Task.CompletedTask;

    internal string? CancelRunning(bool holdRestart = false)
    {
        var cancellation = Volatile.Read(ref _runningCancellation);
        if (cancellation is null) return null;
        if (holdRestart) _restartHeld = true;
        return cancellation.Cancel();
    }

    internal void ReleaseRestartHold() => _restartHeld = false;

    internal async Task CancelAndDrainAsync()
    {
        CancelRunning();
        try { await DrainAsync().ConfigureAwait(false); }
        finally { ReleaseRestartHold(); }
    }

    internal (int Live, int Backstop) ResumeRelaunchDrain(Func<int> countOtherGates)
    {
        ReleaseRestartHold();
        var otherGates = countOtherGates();
        var backstopGates = RunningCount;
        return (otherGates + backstopGates, backstopGates);
    }

    internal void CancelForRelaunchDrainTimeout(int liveGates, int backstopGates, int tick)
    {
        if (backstopGates == 0 || liveGates != backstopGates) return;
        var requestId = CancelRunning(holdRestart: true);
        if (requestId is not null)
            progress($"SELECTED_EXECUTION_BACKSTOP result=cancel-requested request={requestId} reason=relaunch-drain-timeout tick={tick}");
        // A request is not completion; the task remains counted until it settles.
        DrainAsync().Wait(TimeSpan.FromSeconds(ConductorBatchLoop.WatchStopPollIntervalSeconds));
    }

    // Completion and cancellation share source ownership. Callbacks run outside the
    // lock, and disposal waits for both users, including a racing completion.
    private sealed class RunningCancellation(string requestId)
    {
        private readonly object _gate = new();
        private readonly CancellationTokenSource _source = new();
        private bool _completed;
        private bool _requested;
        private bool _cancelling;
        internal CancellationToken Token => _source.Token;

        internal string? Cancel()
        {
            lock (_gate)
            {
                if (_completed || _requested) return null;
                _requested = _cancelling = true;
            }
            try { _source.Cancel(); }
            catch (Exception) { /* Callback failures cannot strand loop cleanup; the signal is set. */ }
            finally
            {
                lock (_gate)
                {
                    _cancelling = false;
                    if (_completed) _source.Dispose();
                }
            }
            return requestId;
        }

        internal void Complete()
        {
            lock (_gate)
            {
                _completed = true;
                if (!_cancelling) _source.Dispose();
            }
        }
    }

    internal void ServiceIdle(OrchestratorWorkspace workspace, ConductorDriver driver, AgentOrchestratorKernel kernel,
        IReadOnlyCollection<Goal> waitingForAcceptance, int width, bool suppressed, int tick)
    {
        var admission = ConductorBatchLoop.DecideFallbackAcceptanceAdmission(driver.ParallelAcceptanceAttemptCoordinator,
            kernel.Goals.ToArray(), driver.GetActiveAcceptanceCohortCapacity(), "", width, tick, ActiveRequestId);
        ServiceAsync(() => GoalWorktrees.ResolveRequiredRef(workspace.ExecutionDirectory, "refs/heads/" + workspace.IntegrationBranch),
            [], waitingForAcceptance, admission.IsAdmitted, suppressed).GetAwaiter().GetResult();
    }

    internal ConductorBatchLoop.LiveAcceptanceCensus ServiceAdmission(OrchestratorWorkspace workspace, IReadOnlyList<Goal> waiting,
        IReadOnlyCollection<Goal> waitingForAcceptance, ConductorBatchLoop.LiveAcceptanceCensus census, int width, bool suppressed)
    {
        ServiceAsync(() => GoalWorktrees.ResolveRequiredRef(workspace.ExecutionDirectory, "refs/heads/" + workspace.IntegrationBranch),
            waiting, waitingForAcceptance, ConductorBatchLoop.DecideLiveAcceptanceAdmission(census, width).IsAdmitted, suppressed).GetAwaiter().GetResult();
        return Reserve(census);
    }

    internal ConductorBatchLoop.LiveAcceptanceCensus Reserve(ConductorBatchLoop.LiveAcceptanceCensus census) =>
        ActiveRequestId is not { } id || census.Occupants.Contains("backstop:" + id) ? census :
            census with { Occupants = census.Occupants.Append("backstop:" + id).ToArray() };

    private async Task ExecuteAsync(SelectedExecutionBackstopRequest request, CancellationToken cancellationToken)
    {
        SelectedExecutionBackstopOutcome outcome;
        try { outcome = await runner.RunAsync(request, cancellationToken).ConfigureAwait(false); }
        catch (Exception ex) { outcome = SelectedExecutionBackstopOutcome.Infrastructure($"runner-{ex.GetType().Name}: {ex.Message}"); }
        await _transitions.WaitAsync().ConfigureAwait(false);
        try
        {
            var state = await store.ReadAsync().ConfigureAwait(false) ?? throw new InvalidDataException("Backstop lost its request.");
            if (state.Request?.Id != request.Id) throw new InvalidDataException("Backstop request changed during execution.");
            var covered = state.RangeLandings.Where(landing => landing.Ordinal <= request.ThroughOrdinal ||
                landing.Commit == request.ToCommit || isAncestor?.Invoke(landing.Commit, request.ToCommit) == true).ToArray();
            request = request with { GoalIds = request.GoalIds.Concat(covered.Select(landing => landing.GoalId))
                .Distinct(StringComparer.Ordinal).ToArray() };
            switch (outcome.Result)
            {
                case "green":
                    state = state with { CoveredCommit = request.ToCommit, Request = null,
                        Landings = state.RangeLandings.Except(covered).ToArray(),
                        LandingInstants = state.RecordedLandingInstants
                            .Where(instant => !covered.Any(landing => landing.Ordinal == instant.Ordinal)).ToArray() };
                    break;
                case "red":
                    var classification = SelectedExecutionBackstopMissClassifier.Classify(outcome.FailingTests, covered);
                    if (!classification.IsMiss)
                    {
                        outcome = outcome with { Result = "infrastructure-failure", InfrastructureReason =
                            classification.UnclassifiableLandings.Count > 0
                                ? "red-unclassifiable-record-unreadable: " + string.Join(',', classification.UnclassifiableLandings
                                    .Select(landing => landing.GoalId + "@" + landing.Commit))
                                : "red-outside-skipped-classes: " + (outcome.FailingTests.Count == 0
                                ? "no failing test identities were available"
                                : "unmatched=" + string.Join("; ", classification.UnmatchedTests)) };
                        break;
                    }
                    var miss = new SelectedExecutionBackstopMissRecord(request.Id, request.FromCommit, request.ToCommit,
                        request.GoalIds, outcome.FailingTests, outcome.FailedChecks,
                        request.GoalIds.Count == 0 ? "no-selected-execution-landing-recorded-in-range" : null);
                    AcceptanceSelectedExecutionSessionSuspension.Write(home, new(miss.Id, stateHome), stateHome);
                    state = state with { Request = null, OpenMiss = miss };
                    break;
                case "infrastructure-failure": break;
                default: throw new InvalidDataException("Unknown backstop outcome: " + outcome.Result);
            }
            var recordedAt = clock();
            // A completed infrastructure attempt rejoins the queue; an interrupted attempt does not.
            if (outcome.Result == "infrastructure-failure")
                state = state with { Request = state.Request! with { QueuedAt = recordedAt, Overtakes = 0, RetryPending = true } };
            await store.WriteAsync(outcome.Result, state, recordedAt).ConfigureAwait(false);
            Report(outcome.Result, request, outcome.FailingTests.Count, outcome.InfrastructureReason);
            if (state.OpenMiss is not null) await ReconcileMissAsync(state).ConfigureAwait(false);
        }
        finally { _transitions.Release(); }
    }

    internal async Task<SelectedExecutionGateMissDetector.Detection> RecordGateMissesAsync(
        Func<IReadOnlyList<SelectedExecutionBackstopLanding>, SelectedExecutionGateMissDetector.Detection> detect)
    {
        await _transitions.WaitAsync().ConfigureAwait(false);
        try
        {
            var state = await store.ReadAsync().ConfigureAwait(false);
            var detection = detect(state?.RangeLandings ?? []);
            if (state is not null && detection.Misses.Count > 0)
            {
                var terminal = (await items.ListAsync().ConfigureAwait(false))
                    .Where(item => CollaborationItemLifecycle.IsTerminal(item.Status))
                    .Select(item => item.CorrelationKey).ToHashSet(StringComparer.Ordinal);
                var pending = detection.Misses.Where(miss => !terminal.Contains(miss.CorrelationKey)).ToArray();
                if (pending.Length > 0)
                {
                    if (state.OpenMiss is null)
                    {
                        state = state with { OpenMiss = pending[0] };
                        await store.WriteAsync("gate-miss", state, clock()).ConfigureAwait(false);
                    }
                    foreach (var miss in pending)
                        await EnsureMissItemAsync(miss).ConfigureAwait(false);
                }
                state = await ReconcileMissAsync(state).ConfigureAwait(false);
            }
            var unclassifiable = detection.UnclassifiableLandings;
            Report(detection.Misses.Count > 0 ? "gate-miss" : "gate-no-miss",
                new("gate", state?.CoveredCommit ?? state?.RangeStartCommit ?? "none",
                    state?.RangeLandings.LastOrDefault()?.Commit ?? "none", 0,
                    detection.Misses.SelectMany(miss => miss.GoalIds).Distinct(StringComparer.Ordinal).ToArray(), clock()),
                detection.Misses.Sum(miss => miss.FailingTests.Count),
                unclassifiable.Count == 0 ? null : "unclassifiable=" + string.Join(',',
                    unclassifiable.Select(landing => landing.GoalId + "@" + landing.Commit)));
            return detection;
        }
        finally { _transitions.Release(); }
    }

    private async Task<SelectedExecutionBackstopState> ReconcileMissAsync(SelectedExecutionBackstopState state)
    {
        if (state.OpenMiss is { } miss)
        {
            var existing = await EnsureMissItemAsync(miss).ConfigureAwait(false);
            if (CollaborationItemLifecycle.IsTerminal(existing.Status))
            {
                state = state with { OpenMiss = null };
                await store.WriteAsync("miss-resolved", state, clock()).ConfigureAwait(false);
                Report("miss-resolved", new(miss.Id, miss.CoveredFromCommit, miss.CoveredToCommit, 0, miss.GoalIds, clock()),
                    miss.FailingTests.Count, "miss=" + miss.Id);
            }
        }

        // Items are the durable set of outstanding misses, including those raised by
        // other gates. OpenMiss is the current representative, not that entire set.
        var open = (await items.ListAsync().ConfigureAwait(false))
            .Where(item => item.CorrelationKey?.StartsWith("selected-execution-backstop-miss:", StringComparison.Ordinal) == true &&
                !CollaborationItemLifecycle.IsTerminal(item.Status))
            .Select(item =>
            {
                var record = JsonSerializer.Deserialize<SelectedExecutionBackstopMissRecord>(item.Body);
                if (record is null || record.CorrelationKey != item.CorrelationKey)
                    throw new InvalidDataException("Open selected execution miss item has an invalid record.");
                return record;
            }).OrderBy(record => record.Id, StringComparer.Ordinal).ToArray();
        if (state.OpenMiss is null && open.Length > 0)
        {
            state = state with { OpenMiss = open[0] };
            await store.WriteAsync("miss-promoted", state, clock()).ConfigureAwait(false);
        }

        // A single-item marker can be dismissed by the gate reader as soon as that
        // item terminates. With several open misses use a collection marker with
        // no individual item: the reader holds suspension until reconciliation can
        // safely project one remaining miss, or clear it when every item terminates.
        if (open.Length > 1)
            AcceptanceSelectedExecutionSessionSuspension.Write(home, new("multiple-open-misses", stateHome), stateHome);
        else if (open.Length == 1)
            AcceptanceSelectedExecutionSessionSuspension.Write(home, new(open[0].Id, stateHome), stateHome);
        else
        {
            var markerPath = AcceptanceSelectedExecutionSessionSuspension.MarkerPath(home, stateHome);
            // A queued request can retain an individual marker, whose terminal item
            // the reader checks. A collection marker has no item to dismiss it.
            if (state.Request is null || File.Exists(markerPath) &&
                JsonSerializer.Deserialize<AcceptanceSelectedExecutionSessionSuspension>(
                    await File.ReadAllTextAsync(markerPath).ConfigureAwait(false))?.MissId == "multiple-open-misses")
                AcceptanceSelectedExecutionSessionSuspension.Write(home, null, stateHome);
        }
        return state;
    }

    private async Task<CollaborationItem> EnsureMissItemAsync(SelectedExecutionBackstopMissRecord miss)
    {
        var existing = (await items.ListAsync().ConfigureAwait(false)).SingleOrDefault(item => item.CorrelationKey == miss.CorrelationKey);
        if (existing is not null) return existing;
        var item = await items.RaiseAsync(CollaborationItemType.Verify, null, "Selected execution backstop found a miss",
            JsonSerializer.Serialize(miss), miss.CorrelationKey).ConfigureAwait(false);
        Report("miss-raised", new(miss.Id, miss.CoveredFromCommit, miss.CoveredToCommit, 0, miss.GoalIds, clock()),
            miss.FailingTests.Count, "item=" + item.Id);
        return item;
    }

    private void Report(string result, SelectedExecutionBackstopRequest request, int failingTests = 0, string? detail = null,
        IReadOnlyList<SelectedExecutionBackstopLanding>? appliedLandings = null, string? rule = null, int? covered = null) =>
        progress($"SELECTED_EXECUTION_BACKSTOP result={result} from={request.FromCommit} to={request.ToCommit} goals=" +
            $"{(request.GoalIds.Count == 0 ? "none" : string.Join(',', request.GoalIds))} failingTests={failingTests}" +
            (rule is null ? "" : $" rule={rule} covered={covered}") +
            (appliedLandings is null ? "" : " applied=" + string.Join(',', appliedLandings
                .Where(landing => landing.Applied).Select(landing => landing.GoalId + "@" + landing.Commit))) +
            (detail is null ? "" : $" detail={detail.Replace('\n', ' ').Replace('\r', ' ')}"));
}
