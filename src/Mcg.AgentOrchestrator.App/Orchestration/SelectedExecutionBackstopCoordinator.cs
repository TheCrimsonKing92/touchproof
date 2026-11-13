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
    Func<string, string, bool>? isAncestor = null, string? stateHome = null)
{
    private readonly SemaphoreSlim _transitions = new(1, 1);
    private Task? _running;
    private string? _activeRequestId;
    private IReadOnlyDictionary<string, (DateTimeOffset ReadyAt, GoalPriority Level)> _admissionOrder =
        new Dictionary<string, (DateTimeOffset, GoalPriority)>();
    internal int RunningCount => _running is { IsCompleted: false } ? 1 : 0;
    internal string? ActiveRequestId => RunningCount == 0 ? null : _activeRequestId;

    internal static SelectedExecutionBackstopCoordinator? Create(OrchestratorWorkspace? workspace,
        ConductorDriver driver, Func<DateTimeOffset> clock, Action<string> progress) =>
        workspace is null || driver._cohortAcceptanceVerifier is null ? null : new(
            new(new SqliteRunEventStore(workspace.RunEventStorePath)), new SelectedExecutionBackstopGateRunner(driver),
            CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory),
            AcceptanceSelectedExecutionSessionSuspension.Home(workspace.ExecutionDirectory, workspace.ProjectHomeDirectoryOrNull),
            clock, progress, (commit, target) => GitCli.Run(workspace.ExecutionDirectory, "merge-base", "--is-ancestor", commit, target).Succeeded,
            workspace.OrchestratorDirectory);

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
        // Missing evidence is reported separately; only the gate's record can establish
        // that a landing applied selection and needs a backstop.
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
            var applied = record?.Applied == true || record?.StructuralCoverageScoped == true;
            if (record is null)
                progress($"SELECTED_EXECUTION_BACKSTOP result=landing-record-unreadable goal={goalId} commit={commit}");
            var landing = new SelectedExecutionBackstopLanding(ordinal, goalId, commit, applied, record is null);
            await store.WriteAsync(record is null ? "landing-record-unreadable" : "landing", state with
            { LastLandingOrdinal = ordinal, Landings = state.RangeLandings.Append(landing).ToArray() }, clock(),
                SelectedExecutionBackstopStore.LandingId(goalId, commit)).ConfigureAwait(false);
        }
        finally { _transitions.Release(); }
    }

    internal async Task ServiceAsync(Func<string> mainHead, IReadOnlyList<Goal> waiting, bool capacityAvailable, bool suppressed)
    {
        await _transitions.WaitAsync().ConfigureAwait(false);
        try
        {
            var state = await store.ReadAsync().ConfigureAwait(false);
            if (state is null) return;
            state = await ReconcileMissAsync(state).ConfigureAwait(false);
            _admissionOrder = waiting.ToDictionary(goal => goal.Id.Value,
                goal => (GoalSchedulingOrder.AcceptanceWaitingAt(goal), GoalEffectivePriorityIndex.EffectiveLevel(goal)), StringComparer.Ordinal);
            if (state.OpenMiss is not null || RunningCount != 0) return;
            if (state.Request is null && state.Uncovered.Count > 0)
            {
                var request = new SelectedExecutionBackstopRequest(Guid.NewGuid().ToString("N"),
                    state.CoveredCommit ?? state.RangeStartCommit, mainHead(), state.LastLandingOrdinal,
                    state.RangeLandings.Select(landing => landing.GoalId).Distinct(StringComparer.Ordinal).ToArray(), clock());
                state = state with { Request = request };
                await store.WriteAsync("queued", state, clock()).ConfigureAwait(false);
                Report("queued", request, detail: state.Uncovered.Any(landing => landing.RecordUnreadable) ? "record-unreadable" : null);
            }
            // A queued entrant keeps its place but covers the newest head when its turn arrives.
            if (state.Request is { Attempts: 0 } queued && mainHead() is { } newest &&
                (newest != queued.ToCommit || queued.ThroughOrdinal != state.LastLandingOrdinal))
            {
                state = state with { Request = queued with { ToCommit = newest, ThroughOrdinal = state.LastLandingOrdinal,
                    GoalIds = state.RangeLandings.Select(landing => landing.GoalId).Distinct(StringComparer.Ordinal).ToArray() } };
                await store.WriteAsync("coalesced", state, clock()).ConfigureAwait(false);
            }
            if (state.Request is not { } pending || suppressed || !capacityAvailable ||
                GoalSchedulingOrder.BeforeBackstop(waiting, pending.QueuedAt, pending.Overtakes).Count > 0) return;
            var started = pending with { Attempts = pending.Attempts + 1 };
            await store.WriteAsync("started", state with { Request = started }, clock()).ConfigureAwait(false);
            Report(pending.Attempts > 0 ? "resumed" : "started", started);
            _activeRequestId = started.Id;
            _running = Task.Run(async () =>
            {
                try { await ExecuteAsync(started).ConfigureAwait(false); }
                catch (Exception ex)
                {
                    // State or notification I/O is retried from the durable request/miss on
                    // the next service tick. It must remain distinct from a gate verdict.
                    try { Report("state-error", started, detail: $"{ex.GetType().Name}: {ex.Message}"); }
                    catch (Exception) { /* A progress sink cannot strand a lifecycle drain. */ }
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

    internal void ServiceIdle(OrchestratorWorkspace workspace, ConductorDriver driver, AgentOrchestratorKernel kernel,
        int width, bool suppressed, int tick)
    {
        var admission = ConductorBatchLoop.DecideFallbackAcceptanceAdmission(driver.ParallelAcceptanceAttemptCoordinator,
            kernel.Goals.ToArray(), driver.GetActiveAcceptanceCohortCapacity(), "", width, tick, ActiveRequestId);
        ServiceAsync(() => GoalWorktrees.ResolveRequiredRef(workspace.ExecutionDirectory, "refs/heads/" + workspace.IntegrationBranch),
            [], admission.IsAdmitted, suppressed).GetAwaiter().GetResult();
    }

    internal ConductorBatchLoop.LiveAcceptanceCensus ServiceAdmission(OrchestratorWorkspace workspace, IReadOnlyList<Goal> waiting,
        ConductorBatchLoop.LiveAcceptanceCensus census, int width, bool suppressed)
    {
        ServiceAsync(() => GoalWorktrees.ResolveRequiredRef(workspace.ExecutionDirectory, "refs/heads/" + workspace.IntegrationBranch),
            waiting, ConductorBatchLoop.DecideLiveAcceptanceAdmission(census, width).IsAdmitted, suppressed).GetAwaiter().GetResult();
        return Reserve(census);
    }

    internal ConductorBatchLoop.LiveAcceptanceCensus Reserve(ConductorBatchLoop.LiveAcceptanceCensus census) =>
        ActiveRequestId is not { } id || census.Occupants.Contains("backstop:" + id) ? census :
            census with { Occupants = census.Occupants.Append("backstop:" + id).ToArray() };

    private async Task ExecuteAsync(SelectedExecutionBackstopRequest request)
    {
        SelectedExecutionBackstopOutcome outcome;
        try { outcome = await runner.RunAsync(request, CancellationToken.None).ConfigureAwait(false); }
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
                        Landings = state.RangeLandings.Except(covered).ToArray() };
                    break;
                case "red":
                    var miss = new SelectedExecutionBackstopMissRecord(request.Id, request.FromCommit, request.ToCommit,
                        request.GoalIds, outcome.FailingTests, outcome.FailedChecks,
                        request.GoalIds.Count == 0 ? "no-selected-execution-landing-recorded-in-range" : null);
                    AcceptanceSelectedExecutionSessionSuspension.Write(home, new(miss.Id, stateHome), stateHome);
                    state = state with { Request = null, OpenMiss = miss };
                    break;
                case "infrastructure-failure": break; // Keep exactly this request pending for normal admission next tick.
                default: throw new InvalidDataException("Unknown backstop outcome: " + outcome.Result);
            }
            await store.WriteAsync(outcome.Result, state, clock()).ConfigureAwait(false);
            Report(outcome.Result, request, outcome.FailingTests.Count, outcome.InfrastructureReason);
            if (state.OpenMiss is not null) await ReconcileMissAsync(state).ConfigureAwait(false);
        }
        finally { _transitions.Release(); }
    }

    private async Task<SelectedExecutionBackstopState> ReconcileMissAsync(SelectedExecutionBackstopState state)
    {
        if (state.OpenMiss is not { } miss)
        {
            if (state.Request is null) AcceptanceSelectedExecutionSessionSuspension.Write(home, null, stateHome);
            return state;
        }
        AcceptanceSelectedExecutionSessionSuspension.Write(home, new(miss.Id, stateHome), stateHome);
        var existing = (await items.ListAsync().ConfigureAwait(false)).SingleOrDefault(item => item.CorrelationKey == miss.CorrelationKey);
        if (existing is null)
            await items.RaiseAsync(CollaborationItemType.Verify, null, "Selected execution backstop found a miss",
                JsonSerializer.Serialize(miss), miss.CorrelationKey).ConfigureAwait(false);
        else if (CollaborationItemLifecycle.IsTerminal(existing.Status))
        {
            state = state with { OpenMiss = null };
            await store.WriteAsync("miss-resolved", state, clock()).ConfigureAwait(false);
            AcceptanceSelectedExecutionSessionSuspension.Write(home, null, stateHome);
        }
        return state;
    }

    private void Report(string result, SelectedExecutionBackstopRequest request, int failingTests = 0, string? detail = null) =>
        progress($"SELECTED_EXECUTION_BACKSTOP result={result} from={request.FromCommit} to={request.ToCommit} goals=" +
            $"{(request.GoalIds.Count == 0 ? "none" : string.Join(',', request.GoalIds))} failingTests={failingTests}" +
            (detail is null ? "" : $" detail={detail.Replace('\n', ' ').Replace('\r', ' ')}"));
}
