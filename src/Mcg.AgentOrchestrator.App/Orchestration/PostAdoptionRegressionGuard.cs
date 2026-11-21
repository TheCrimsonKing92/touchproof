using System.Runtime.CompilerServices;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using GoalDecision = Mcg.AgentOrchestrator.App.Orchestration.PostAdoptionRegressionEvaluator.GoalDecision;
using HandleStatus = Mcg.AgentOrchestrator.App.Orchestration.PostAdoptionRegressionReverter.HandleStatus;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal interface IPostAdoptionRegressionTickObserver
{
    void ObserveGoalResult(int tick, DateTimeOffset now, string goalId, PolicyDecisionRecord? decision,
        bool gateRunning, bool gateSlotFree, int verifiedGoalCount);
}

internal sealed class PostAdoptionRegressionGuard : IPostAdoptionRegressionTickObserver
{
    private static readonly ConditionalWeakTable<ConductorDriver, Lazy<IPostAdoptionRegressionTickObserver?>> Observers = new();

    private readonly object _sync = new();
    private readonly PostAdoptionRegressionEvaluator _evaluator = new();
    private readonly PostAdoptionRegressionReverter _reverter;
    private readonly Action<string> _reportInactive;
    private string? _landingCommit;
    private GoalDecision[]? _baseline;
    private DateTimeOffset _adoptedAt;
    private int _ticksSinceAdoption;
    private bool _inactiveReported;
    private PendingTick? _pending;

    private sealed record PendingTick(int Number, List<GoalDecision> Decisions,
        bool GateRunning, bool GateSlotFree, int VerifiedGoalCount);

    internal PostAdoptionRegressionGuard(string? landingCommit, IReadOnlyList<GoalDecision>? baseline,
        DateTimeOffset adoptedAt, PostAdoptionRegressionReverter reverter, Action<string> reportInactive)
    {
        _reverter = reverter;
        _reportInactive = reportInactive;
        Adopt(landingCommit, baseline, adoptedAt);
    }

    // A new adoption replaces the observation window and preserves the incident claim.
    internal void Adopt(string? landingCommit, IReadOnlyList<GoalDecision>? baseline, DateTimeOffset adoptedAt)
    {
        lock (_sync)
        {
            _landingCommit = landingCommit;
            _baseline = baseline?.ToArray();
            _adoptedAt = adoptedAt;
            _ticksSinceAdoption = 0;
            _pending = null;
            _inactiveReported = false;
        }
    }

    internal HandleStatus? ObserveTick(IReadOnlyList<GoalDecision> decisions, bool gateRunning,
        bool gateSlotFree, int verifiedGoalCount, DateTimeOffset now)
    {
        lock (_sync)
            return EvaluateTick(decisions, gateRunning, gateSlotFree, verifiedGoalCount, now);
    }

    public void ObserveGoalResult(int tick, DateTimeOffset now, string goalId, PolicyDecisionRecord? decision,
        bool gateRunning, bool gateSlotFree, int verifiedGoalCount)
    {
        lock (_sync)
        {
            if (!IsActive()) return;

            var pending = _pending;
            if (pending is not null && tick < pending.Number)
                throw new InvalidOperationException($"Post-adoption guard received tick {tick} after tick {pending.Number}.");

            // The landing stage reports individual goals. The next observed tick closes the prior
            // batch. A trailing batch stays buffered if the landing stage is never visited again.
            if (pending is not null && tick > pending.Number)
            {
                _pending = null;
                var skippedTicks = tick - pending.Number - 1;
                if (skippedTicks == 0)
                {
                    // A gate may have started since the buffered results were recorded.
                    EvaluateTick(pending.Decisions, pending.GateRunning || gateRunning, pending.GateSlotFree,
                        pending.VerifiedGoalCount, now);
                }
                else
                {
                    // A gap leaves no current evidence that the buffered blocker still applies.
                    _ticksSinceAdoption += skippedTicks + 1;
                }
                pending = null;
            }

            var decisions = pending?.Decisions ?? [];
            decisions.Add(new(goalId, EligibleBeforeAdoption: false, decision));
            _pending = new(tick, decisions, gateRunning, gateSlotFree, verifiedGoalCount);
        }
    }

    private HandleStatus? EvaluateTick(IReadOnlyList<GoalDecision> decisions, bool gateRunning,
        bool gateSlotFree, int verifiedGoalCount, DateTimeOffset now)
    {
        if (!IsActive()) return null;

        var observation = new PostAdoptionRegressionEvaluator.Observation(_landingCommit!, _baseline!,
            decisions, ++_ticksSinceAdoption, now - _adoptedAt, gateRunning, gateSlotFree, verifiedGoalCount);
        return _reverter.Handle(_evaluator.Evaluate(observation)).Status;
    }

    private bool IsActive()
    {
        var reason = InactiveReason(_landingCommit, _baseline);
        if (reason is null) return true;
        if (!_inactiveReported)
        {
            _reportInactive(InactiveDetail(_landingCommit, reason));
            _inactiveReported = true;
        }
        return false;
    }

    internal static void Attach(ConductorDriver driver, IPostAdoptionRegressionTickObserver observer) =>
        Observers.Add(driver, new(() => observer));

    internal static ConductorAdvanceResult ObserveLandingResult(ConductorDriver driver, Goal goal,
        ConductorAdvanceResult landing, bool isConductorTick, int tick, Func<DateTimeOffset> utcNow,
        ConductorParallelAcceptanceAttemptCoordinator coordinator)
    {
        if (!isConductorTick) return landing;
        try
        {
            if (For(driver) is { } observer)
            {
                var reserved = coordinator.GetAllCapacityReservingAttempts();
                var activeCohorts = driver.GetActiveAcceptanceCohortCapacity().ActiveRootCount;
                observer.ObserveGoalResult(tick, utcNow(), goal.Id.Value,
                    (landing.Outcome as ConductorAdvanceOutcome.Held)?.Decision,
                    gateRunning: reserved.Count > 0 || activeCohorts > 0,
                    gateSlotFree: reserved.Count + activeCohorts < driver.GetAcceptanceSlotCount(goal),
                    verifiedGoalCount: (driver._cohortKernel ?? driver._conductorTickKernel)?.Goals.Count(g => g.Status == GoalStatus.Verified) ?? 0);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Console.Error.WriteLine($"POST_ADOPTION_GUARD_OBSERVATION_FAILED tick={tick} {ex.Message}");
        }
        return landing;
    }

    private static IPostAdoptionRegressionTickObserver? For(ConductorDriver driver)
    {
        if (Observers.TryGetValue(driver, out var registered)) return registered.Value;
        return driver._cohortWorkspace is { } workspace
            ? Observers.GetValue(driver, _ => new(() => ReportProductionInactivity(workspace))).Value
            : null;
    }

    private static IPostAdoptionRegressionTickObserver? ReportProductionInactivity(OrchestratorWorkspace workspace)
    {
        // The pre-relaunch snapshot is supplied by a later slice. Current results cannot serve as evidence of prior eligibility.
        var landingCommit = ReadRunningBuildCommit(workspace.ConductEventsLogPath);
        var detail = InactiveDetail(landingCommit, InactiveReason(landingCommit, baseline: null)!);
        if (landingCommit is null)
            Console.Error.WriteLine(detail);
        else
            new ConductEventLogWriter(workspace.ConductEventsLogPath).Append("post-adoption-regression", null, detail);
        return null;
    }

    private static string? ReadRunningBuildCommit(string path)
    {
        var state = ConductorRelaunchObservation.Empty;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            while (reader.ReadLine() is { } line)
                if (ConductEventsLogTail.ParseLine(line) is { } row)
                    state = ConductorRelaunchLogFold.Apply(state, row.At, row.Detail);
            return state.RunningBuildCommit;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? InactiveReason(string? landingCommit, IReadOnlyList<GoalDecision>? baseline) =>
        string.IsNullOrWhiteSpace(landingCommit) ? "no-landing-commit" :
        baseline is not { Count: > 0 } ? "no-adoption-baseline" : null;

    private static string InactiveDetail(string? landingCommit, string reason) =>
        $"POST_ADOPTION_GUARD_INACTIVE reason={reason} landing={landingCommit ?? "unknown"}";
}
