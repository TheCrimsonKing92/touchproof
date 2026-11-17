using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Mcg.AgentOrchestrator.App.OwnerConsole;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Landing retention and miss reconciliation share serialized durable state transitions.
internal sealed class SelectedExecutionBackstopCoordinator(
    SelectedExecutionBackstopStore store, ICollaborationItemStore items, string home,
    Func<DateTimeOffset> clock, Action<string> progress, string? stateHome = null)
{
    internal const int MaxRetainedLandings = 200;
    private readonly SemaphoreSlim _transitions = new(1, 1);

    internal static SelectedExecutionBackstopCoordinator? Create(OrchestratorWorkspace? workspace,
        ConductorDriver driver, Func<DateTimeOffset> clock, Action<string> progress) =>
        workspace is null || driver._cohortAcceptanceVerifier is null ? null : new(
            new(new SqliteRunEventStore(workspace.RunEventStorePath)),
            CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory),
            AcceptanceSelectedExecutionSessionSuspension.Home(workspace.ExecutionDirectory, workspace.ProjectHomeDirectoryOrNull),
            clock, progress, workspace.OrchestratorDirectory);

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
        // Only the gate's record establishes applied selection. Retain unreadability
        // so the miss detector can report landings it cannot classify.
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
            var retained = state.RangeLandings.Append(landing).TakeLast(MaxRetainedLandings).ToArray();
            var retainedOrdinals = retained.Select(value => value.Ordinal).ToHashSet();
            await store.WriteAsync(record is null ? "landing-record-unreadable" : "landing", state with
            { LastLandingOrdinal = ordinal, Landings = retained,
                LandingInstants = state.RecordedLandingInstants.Append(new(ordinal, recordedAt))
                    .Where(instant => retainedOrdinals.Contains(instant.Ordinal)).ToArray() }, recordedAt,
                SelectedExecutionBackstopStore.LandingId(goalId, commit)).ConfigureAwait(false);
        }
        finally { _transitions.Release(); }
    }

    internal async Task ServiceAsync()
    {
        await _transitions.WaitAsync().ConfigureAwait(false);
        try
        {
            var state = await store.ReadAsync().ConfigureAwait(false);
            if (state is null) return;
            if (state.Request is { } legacy)
            {
                state = state with { Request = null };
                await store.WriteAsync("legacy-request-cleared", state, clock()).ConfigureAwait(false);
                progress($"SELECTED_EXECUTION_BACKSTOP result=legacy-request-cleared request={legacy.Id}");
            }
            await ReconcileMissAsync(state).ConfigureAwait(false);
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
            AcceptanceSelectedExecutionSessionSuspension.Write(home, null, stateHome);
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

    private void Report(string result, SelectedExecutionBackstopRequest request, int failingTests = 0, string? detail = null) =>
        progress($"SELECTED_EXECUTION_BACKSTOP result={result} from={request.FromCommit} to={request.ToCommit} goals=" +
            $"{(request.GoalIds.Count == 0 ? "none" : string.Join(',', request.GoalIds))} failingTests={failingTests}" +
            (detail is null ? "" : $" detail={detail.Replace('\n', ' ').Replace('\r', ' ')}"));
}
