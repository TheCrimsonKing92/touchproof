using System.Diagnostics;
using System.Globalization;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;
using static Mcg.AgentOrchestrator.App.Orchestration.ConductorRelaunchDrainIntentBoundary;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal readonly record struct ParallelAcceptanceOldestWaiterObservation(
    Goal? Waiter,
    int ConsecutiveTicks)
{
    internal bool IsStalled =>
        Waiter is not null &&
        ConsecutiveTicks >= ConductorBatchLoop.ParallelAcceptanceOldestWaiterStallTickThreshold;
}

internal sealed partial class ConductorBatchLoop
{
    internal const string StopFileName = ".conduct-stop";
    internal const int DefaultMaxVerifyRetries = 2;
    internal const int DefaultWatchIntervalSeconds = 15;
    internal const int DefaultBlockedRecheckCycles = 2;
    internal static readonly TimeSpan DefaultBlockedRecheckHeartbeatInterval = TimeSpan.FromMinutes(5);
    internal const int WatchStopPollIntervalSeconds = 5;
    internal const int QuietSummaryEveryTicks = 20;
    internal const int DefaultMaxBusyWriteAttempts = 1;
    internal const int DispatchRecordContentionSkipLimit = 5;
    internal const int SlotContentionAttentionHoldLimit = 5;
    internal const int DefaultGracefulDetachCheckpointAttempts = 3;
    internal const int JanitorialFailureEscalationThreshold = 3;
    internal const int ParallelAcceptanceTransientFailureCap = 3;
    internal const int ParallelAcceptanceBoundedOvertakeLimit = 1;
    // Tick 1 permits the single bounded overtake, tick 2 preserves the oldest waiter's
    // guaranteed turn, and tick 3 is the first bypass. A waiter with a live attempt is
    // excluded by the selector; admission on the threshold tick sets the served guard.
    internal const int ParallelAcceptanceOldestWaiterStallTickThreshold = 3;
    // Parallel-acceptance WIDTH governs logical, per-tick landing-gate admission. BuildConcurrencySlotCount
    // separately governs isolated build environments and their OS file-lock permits, which a gate holds only
    // for its shared prebuild. Both defaults remain 2 by current tuning, not by derivation. Before raising width,
    // measure intermittent acceptance-gate failures per attempt stratified by peak concurrent gate count, plus
    // p95 Verified-to-gate-start time and peak CPU, working set, and disk-queue depth; widen the slot-heartbeat
    // enumeration first if width will exceed build slots. The maximum rejects corrupt overrides, not endorses 4.
    internal const int DefaultParallelAcceptanceCapacity = 2;
    internal const int MaxParallelAcceptanceCapacity = 4;
    // Paid-worker ADMISSION pool that the gate-slot reservation (ConductorDriver worker-cap)
    // draws from. Deliberately INDEPENDENT of build concurrency / acceptance width: coding
    // workers do not hold build slots. On the 47.9-GiB operator host, a reviewed paid-worker
    // dispatch peaked at 1,750,343,680 bytes. Limiting workers to one third of physical memory
    // yields floor(51,385,864,192 / 3 / 1,750,343,680) = 9, leaving two thirds for the OS,
    // conductor, builds, and acceptance gates (observed 2026-08-08). Provider cooldowns are
    // handled separately; revisit this fixed limit when memory-aware admission is implemented.
    // Do NOT tie this to BuildConcurrencySlotCount or DefaultParallelAcceptanceCapacity.
    internal const int WorkerAdmissionCapacity = 9;
    internal const int DefaultUnscopedStallTickThreshold = 3;
    internal static readonly TimeSpan DefaultGoalStallThreshold = TimeSpan.FromMinutes(10);
    internal const bool DefaultSelfRelaunchEnabled = true;
    internal const string SetAsideSelfClearDecisionPrefix = "Set-aside self-cleared:";
    private readonly Func<AgentOrchestratorKernel, IReadOnlySet<string>, bool, TerminalGoalSweepResult?> _sweep;
    private readonly LandingRelaunchDecisions _landingRelaunchDecisions;
    private readonly Action<AgentOrchestratorKernel, Goal> _reapGoalRunningDispatches;
    private readonly Action<AgentOrchestratorKernel, Goal> _detachGoalRunningDispatches;
    private readonly Action<AgentOrchestratorKernel> _recoverInterruptedDispatches;
    private readonly GoalDispatchRefresh _refreshGoalDispatchesBeforeAdvance;
    private readonly ConductorWatchProgressReporter _watchProgressReporter;
    private readonly OperatorIntentCoordinator? _operatorIntents;
    private readonly Func<AgentOrchestratorKernel, GoalId, bool, bool>? _prepareOperatorIntentTarget;
    private readonly ProgressiveReviewGlanceCoordinator? _progressiveReviewGlances;
    private readonly ProgressiveReviewSteeringCoordinator? _progressiveReviewSteering;
    private readonly Func<ConductorLoopHandoffRequest, ConductorLoopHandoffResult>? _handoffOnMaxDuration;
    private readonly Func<ConductorSelfRelaunchRequest, ConductorSelfRelaunchResult>? _selfRelaunch;
    private readonly bool _selfRelaunchEnabled;
    private readonly HomeBuildRelaunchCoordinator? _homeBuildRelaunchCoordinator;
    private readonly Func<IReadOnlyList<string>>? _homeBuildIdleBlockers;
    private readonly PostLandingCanaryCoordinator? _postLandingCanary;
    private readonly TimeSpan _canaryDrainBound;
    private readonly TimeSpan _canaryTerminationGrace;
    private readonly TimeProvider _canaryDrainClock;
    private readonly TimeProvider _drainClock;
    private SelectedExecutionBackstopCoordinator? _selectedExecutionBackstop;
    private MainFullVerificationCoordinator? _mainFullVerification;
    private readonly PromptRolloutWatchCoordinator? _promptRolloutWatch;
    private readonly AcceptanceEngineCircuitBreaker? _acceptanceEngineCircuit;
    private readonly ConductEventLogWriter? _conductEventLogWriter;
    private readonly ConductorLifecycleRecorder? _lifecycleRecorder;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly InvalidatedAcceptanceAttemptStopPolicy _invalidatedAttemptStopPolicy;
    internal Func<ConductorParallelAcceptanceAttempt, InvalidatedAcceptanceAttemptStopResult>? InvalidatedAttemptStop { get; init; }
    internal SelectedExecutionBackstopCoordinator? SelectedExecutionBackstop { init => _selectedExecutionBackstop = value; }
    private readonly Func<string?> _readRelaunchDrainCap;
    private readonly Func<int> _countLiveGroupedGates;
    private readonly Func<double> _writeJitter;
    private readonly Func<string, ConductorGoalReloadObservation> _goalReloadObservation;
    private readonly TimeSpan _blockedRecheckHeartbeatInterval;
    private readonly OrchestratorWorkspace? _workspace;
    private readonly Action<string> _loadTrxCoherenceVerdicts;
    // Janitorial phases run only on the conductor loop thread; acceptance work never mutates this state.
    private readonly Dictionary<string, int> _consecutiveJanitorialFailures = new(StringComparer.Ordinal);
    private readonly ConductorSlotContentionHolds _slotContentionHolds = new();
    private readonly Dictionary<string, long> _tickPhaseElapsedMs = new(StringComparer.Ordinal);
    private readonly Action<string>? _janitorialPhaseProbe;
    private readonly Func<long> _janitorialTimestamp;
    internal static readonly AsyncLocal<ConductEventLogWriter?> CurrentConductEventLogWriter = new();
    private static readonly AsyncLocal<RetryDiagnosticCoalescer?> CurrentRetryDiagnostics = new();

    private readonly Func<AgentOrchestratorKernel, DateTimeOffset, IReadOnlyList<GoalId>>? _providerHoldProbe;

    public ConductorBatchLoop(
        Action<AgentOrchestratorKernel>? sweep = null,
        Action<AgentOrchestratorKernel, Goal>? reapGoalRunningDispatches = null,
        Action<AgentOrchestratorKernel, Goal>? detachGoalRunningDispatches = null,
        Action<AgentOrchestratorKernel>? recoverInterruptedDispatches = null,
        GoalDispatchRefresh? refreshGoalDispatchesBeforeAdvance = null,
        ConductorWatchProgressReporter? watchProgressReporter = null,
        Func<AgentOrchestratorKernel, TerminalGoalSweepResult?>? measuredSweep = null,
        Func<ConductorLoopHandoffRequest, ConductorLoopHandoffResult>? handoffOnMaxDuration = null,
        ConductEventLogWriter? conductEventLogWriter = null,
        Func<DateTimeOffset>? utcNow = null,
        OperatorIntentCoordinator? operatorIntents = null,
        ProgressiveReviewGlanceCoordinator? progressiveReviewGlances = null,
        ProgressiveReviewSteeringCoordinator? progressiveReviewSteering = null,
        Func<ConductorSelfRelaunchRequest, ConductorSelfRelaunchResult>? selfRelaunch = null,
        bool selfRelaunchEnabled = DefaultSelfRelaunchEnabled,
        PostLandingCanaryCoordinator? postLandingCanary = null,
        AcceptanceEngineCircuitBreaker? acceptanceEngineCircuit = null,
        Func<string, ConductorGoalReloadObservation>? goalReloadObservation = null,
        ConductorLifecycleRecorder? lifecycleRecorder = null,
        Func<double>? writeJitter = null,
        TimeSpan? blockedRecheckHeartbeatInterval = null,
        Func<AgentOrchestratorKernel, IReadOnlySet<string>, bool, TerminalGoalSweepResult?>? measuredSweepWithCheckpointHolds = null,
        OrchestratorWorkspace? workspace = null,
        Action<string>? janitorialPhaseProbe = null,
        Func<long>? janitorialTimestamp = null,
        Func<string?>? readRelaunchDrainCap = null,
        PromptRolloutWatchCoordinator? promptRolloutWatch = null,
        Func<TimeSpan>? processCpuTime = null,
        Action<string>? loadTrxCoherenceVerdicts = null,
        Func<int>? countLiveGroupedGates = null,
        Func<AgentOrchestratorKernel, GoalId, bool, bool>? prepareOperatorIntentTarget = null,
        HomeBuildAdoptionDetector? homeBuildAdoption = null,
        Func<ConductorSelfRelaunchRequest, ConductorSelfRelaunchResult>? homeBuildRelaunch = null,
        Func<IReadOnlyList<string>>? homeBuildIdleBlockers = null,
        Func<AgentOrchestratorKernel, DateTimeOffset, IReadOnlyList<GoalId>>? providerHoldProbe = null,
        TimeSpan? canaryDrainBound = null,
        TimeSpan? canaryTerminationGrace = null,
        TimeProvider? canaryDrainClock = null,
        TimeProvider? drainClock = null,
        Func<IReadOnlyList<string>>? reconcileDrainedSweepRemedies = null)
    {
        _sweep = measuredSweepWithCheckpointHolds is not null
            ? measuredSweepWithCheckpointHolds
            : measuredSweep is not null
                ? (kernel, _, _) => measuredSweep(kernel)
                : (kernel, _, _) => InvokeLegacySweep(kernel);
        _reconcileDrainedSweepRemedies = reconcileDrainedSweepRemedies;
        TerminalGoalSweepResult? InvokeLegacySweep(AgentOrchestratorKernel kernel)
        {
            sweep?.Invoke(kernel);
            return null;
        }
        _reapGoalRunningDispatches = reapGoalRunningDispatches ?? ((_, _) => { });
        _detachGoalRunningDispatches = detachGoalRunningDispatches ?? _reapGoalRunningDispatches;
        _recoverInterruptedDispatches = kernel => { StaleDispatchProcessReconciler.Reconcile(kernel, workspace); recoverInterruptedDispatches?.Invoke(kernel); };
        _refreshGoalDispatchesBeforeAdvance = refreshGoalDispatchesBeforeAdvance ?? ((_, _) => null);
        _drainClock = drainClock ?? TimeProvider.System;
        _watchProgressReporter = watchProgressReporter ?? new ConductorWatchProgressReporter();
        _operatorIntents = operatorIntents;
        _prepareOperatorIntentTarget = prepareOperatorIntentTarget;
        _progressiveReviewGlances = progressiveReviewGlances;
        _progressiveReviewSteering = progressiveReviewSteering;
        _handoffOnMaxDuration = handoffOnMaxDuration;
        _selfRelaunch = selfRelaunch;
        _selfRelaunchEnabled = selfRelaunchEnabled;
        _homeBuildRelaunchCoordinator = homeBuildAdoption is not null && homeBuildRelaunch is not null
            ? new HomeBuildRelaunchCoordinator(homeBuildAdoption, homeBuildRelaunch) : null;
        _homeBuildIdleBlockers = homeBuildIdleBlockers;
        _providerHoldProbe = providerHoldProbe;
        _postLandingCanary = postLandingCanary;
        _canaryDrainBound = canaryDrainBound ?? TimeSpan.FromSeconds(CanaryTaskDrain.DefaultBoundSeconds);
        _canaryTerminationGrace = canaryTerminationGrace ?? TimeSpan.FromSeconds(CanaryTaskDrain.DefaultTerminationGraceSeconds);
        _canaryDrainClock = canaryDrainClock ?? TimeProvider.System;
        _acceptanceEngineCircuit = postLandingCanary?.CircuitBreaker ?? acceptanceEngineCircuit;
        _conductEventLogWriter = conductEventLogWriter;
        _lifecycleRecorder = lifecycleRecorder;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _invalidatedAttemptStopPolicy = new(_utcNow);
        _processCpuTime = processCpuTime ?? (() => Environment.CpuUsage.TotalTime);
        _readRelaunchDrainCap = readRelaunchDrainCap ?? (() => Environment.GetEnvironmentVariable(RelaunchDrainCapEnvironmentVariable));
        _countLiveGroupedGates = countLiveGroupedGates ?? (() => 0);
        _writeJitter = writeJitter ?? Random.Shared.NextDouble;
        _goalReloadObservation = goalReloadObservation ?? (_ => new ConductorGoalReloadObservation.Missing());
        _blockedRecheckHeartbeatInterval = blockedRecheckHeartbeatInterval ?? DefaultBlockedRecheckHeartbeatInterval;
        _workspace = workspace;
        _landingRelaunchDecisions = new(selfRelaunchEnabled, _selfRelaunch is not null, _postLandingCanary is null ? null : receipt => _postLandingCanary.LaunchLandingAsync(receipt));
        _loadTrxCoherenceVerdicts = loadTrxCoherenceVerdicts ?? (directory =>
        {
            if (workspace is null) return;
            TrxCoherenceVerdictStore.Load(directory);
            TrxCoherenceVerdictStore.EnableAppend(directory);
        });
        _promptRolloutWatch = promptRolloutWatch ?? PromptRolloutWatchCoordinator.CreateDefault(workspace, line => EmitProgress(line));
        _janitorialPhaseProbe = janitorialPhaseProbe;
        _janitorialTimestamp = janitorialTimestamp ?? Stopwatch.GetTimestamp;
        if (_blockedRecheckHeartbeatInterval <= TimeSpan.Zero || _blockedRecheckHeartbeatInterval > TimeSpan.FromMinutes(10))
        {
            throw new ArgumentOutOfRangeException(nameof(blockedRecheckHeartbeatInterval));
        }
    }

    public BatchLoopSummary Run(
        AgentOrchestratorKernel kernel,
        ConductorDriver driver,
        ConductorAutonomyPolicy policy,
        string stopFilePath,
        int? maxIterations = null,
        int maxVerifyRetries = DefaultMaxVerifyRetries,
        TimeSpan? watchInterval = null,
        Action<BatchTickSummary>? onTick = null,
        Func<TimeSpan, bool>? sleepFunc = null,
        IConductorWakeSignal? wakeSignal = null,
        TimeSpan? maxDuration = null,
        string? onlyGoalId = null,
        Action<AgentOrchestratorKernel>? persistTick = null,
        bool keepAliveWhenIdle = false,
        Action<AgentOrchestratorKernel, IReadOnlyCollection<GoalId>>? persistGoalTick = null,
        Func<AgentOrchestratorKernel, IReadOnlyList<ConductorOperatorDispositionSnapshot>>? buildOperatorDispositions = null,
        bool quiet = false,
        TimeSpan? stallWarningThreshold = null,
        TimeSpan? goalStallThreshold = null,
        int unscopedStallTickThreshold = DefaultUnscopedStallTickThreshold,
        Action<TimeSpan>? busyWriteDelay = null,
        string? journalMode = null,
        string policySource = "preset",
        Func<ConductorPolicyResolution>? reloadPolicy = null,
        Func<AgentOrchestratorKernel, IReadOnlyCollection<GoalId>, IReadOnlyList<GoalSnapshotCheckpointResult>>? checkpointGoalTick = null,
        Func<bool>? hasTransientLoadHold = null,
        TimeSpan? maxDurationDeferralCeiling = null,
        Action<bool>? onMaxDurationDeferralStateChanged = null,
        bool emitActivationHeartbeat = false,
        Action? observeTickWithoutDispatch = null)
    {
        var watchSleep = new ConductorWatchSleep(stopFilePath, ignoreInitialStopFile: onlyGoalId is not null);
        var drain = new ConductorDrainSignal(ConductorDrainSignal.PathFor(stopFilePath), _drainClock,
            ignoreInitial: onlyGoalId is not null);
        var leaseDirectory = Path.GetDirectoryName(Path.GetFullPath(stopFilePath)) ?? Directory.GetCurrentDirectory();
        var leaseAcquisition = AcquireActiveDatabaseLeases(leaseDirectory, busyWriteDelay);
        if (!leaseAcquisition.Succeeded)
            return leaseAcquisition.DeferredSummary!;

        using var activeConductorLease = leaseAcquisition.StateLease!;
        using var activeRunEventLease = leaseAcquisition.RunEventLease!;
        using var resolverObservation = driver.PreTesterResolverObservation;
        if (watchSleep.IsIgnoringInitialStopFile)
            Console.WriteLine($"[conduct --watch] The existing repo-wide stop file is ignored for named goal {ShortGoalId(onlyGoalId!)}.");
        using var activationAdmissionHealth = BeginActivationAdmission(emitActivationHeartbeat);
        checkpointGoalTick = ResetLandingTickSave(checkpointGoalTick);
        _consecutiveJanitorialFailures.Clear();
        _slotContentionHolds.Clear();
        ResetWorkerCapacityWatch();
        var previousConductEventLogWriter = leaseAcquisition.PreviousConductEventLogWriter;
        var previousRetryDiagnostics = CurrentRetryDiagnostics.Value;
        var previousSuccessfulLandingSink = driver.SuccessfulLandingSink;
        var previousDispatchRecordWriteSucceededSink = driver.DispatchRecordWriteSucceededSink;
        var previousLandingMutationBlocker = driver.LandingMutationBlocker;
        var canaryDrain = new CanaryLoopDrain(_postLandingCanary, _canaryDrainBound,
            _canaryTerminationGrace, _canaryDrainClock, line => EmitProgress(line));
        CurrentRetryDiagnostics.Value = new RetryDiagnosticCoalescer(_utcNow);
        var totalTicks = 0;
        var blockedRecheckCycles = 0;
        var totalBlockedRechecks = 0;
        var dispatchRecordWriteSkips = new Dictionary<string, int>(StringComparer.Ordinal);
        var checkpointHeldGoals = new Dictionary<string, GoalSnapshotCheckpointResult>(StringComparer.Ordinal);
        string? pendingWakeMaintenanceKind = null;
        var blockedRecheckRecurrences = new Dictionary<string, BlockedRecheckRecurrence>(StringComparer.Ordinal);
        DateTimeOffset? lastBlockedRecheckHeartbeatAt = null;
        string? stopReason = null;
        ConductorLifecycleSession? lifecycleSession = null;

        void StopLoop(string reason, string? detail = null)
        {
            stopReason ??= reason;
            lifecycleSession?.Stop(reason, totalTicks, detail);
            EmitProgress($"LOOP_STOP tick={totalTicks} rechecks={totalBlockedRechecks} reason={reason}" +
                         (string.IsNullOrWhiteSpace(detail) ? string.Empty : $" {detail}"));
        }
        void CompleteActivationTick(int tick)
        {
            EmitActivationAdmissionProbeAndHealth(emitActivationHeartbeat, tick);
            EmitActivationTickEnd(emitActivationHeartbeat, tick);
        }

        driver.DispatchRecordWriteSucceededSink = goalId =>
        {
            dispatchRecordWriteSkips.Remove(goalId.Value);
            previousDispatchRecordWriteSucceededSink?.Invoke(goalId);
        };
        driver.LandingMutationBlocker = () =>
        {
            var existingBlock = previousLandingMutationBlocker?.Invoke();
            if (!string.IsNullOrWhiteSpace(existingBlock))
            {
                return existingBlock;
            }

            var snapshot = (_acceptanceEngineCircuit ?? _postLandingCanary?.CircuitBreaker)?.Read();
            if (snapshot is null)
            {
                return null;
            }

            var decision = AcceptanceEngineAcceptanceGate.Decide(
                snapshot.Health,
                AcceptanceEngineAcceptanceGate.DefaultUnavailablePolicy);
            return decision.Allowed
                ? null
                : $"{decision.Reason}; landing={snapshot.LandingSha ?? "unknown-sha"}; " +
                  $"failure={snapshot.FailureReason ?? "canary-pending"}";
        };
        try
        {
        var excludedGoals = new HashSet<string>(StringComparer.Ordinal);
        var setAsideGoals = new Dictionary<string, BatchSetAsideEntry>(StringComparer.Ordinal);
        var selfClearedSetAsideEntries = new Dictionary<string, BatchSetAsideEntry>(StringComparer.Ordinal);
        var readmittedRetryReservations = new HashSet<string>(StringComparer.Ordinal);
        var completedGoals = new HashSet<string>(StringComparer.Ordinal);
        var escalatedGoals = new HashSet<string>(StringComparer.Ordinal);
        var dependencyEscalatedGoals = new HashSet<string>(StringComparer.Ordinal);
        var advanceFaultRetries = new HashSet<(string GoalId, string Fingerprint)>();
        var reapedGoals = new HashSet<string>(StringComparer.Ordinal);
        var retryCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var lastGoalDisposition = new Dictionary<string, string>(StringComparer.Ordinal);
        var unscopedDispatchableTicks = new Dictionary<string, int>(StringComparer.Ordinal);
        var goalProjectionCache = new GoalProjectionCache();
        var clampedRechecks = new HashSet<RetryDiagnosticKey>();
        var totalAdvanced = 0;
        var totalHeld = 0;
        var totalEscalated = 0;
        var totalRetried = 0;
        var totalDone = 0;
        var landedGoalIds = new HashSet<string>(StringComparer.Ordinal);
        var stopRequested = false;
        var maxDurationReached = false;
        var isDeferringMaxDurationStop = false;
        var maxDurationDeferralAnnounced = false;
        DateTimeOffset? maxDurationDeferralStartedAt = null;
        HashSet<string>? maxDurationDeferredAttemptIds = null;
        ConductorSelfRelaunchRequest? pendingSelfRelaunch = null;
        ConductorSelfRelaunchRequest? deferredSelfRelaunch = null;
        int? selfRelaunchRetryAfterTick = null;
        DateTimeOffset? selfRelaunchDrainStartedAt = null;
        var selfRelaunchDrainCap = new SelfRelaunchDrainCapState(
            ResolveRelaunchDrainCap(_readRelaunchDrainCap()));
        ConductorLoopHandoffResult? selfRelaunchHandoff = null;
        HomeBuildAdoptionRecord? handedOffHomeBuild = null;
        var started = _utcNow();
        _cohortGatherDeadline = ComputeCohortGatherDeadline(started, maxDuration);
        var effectiveGoalStallThreshold = goalStallThreshold ?? DefaultGoalStallThreshold;
        if (effectiveGoalStallThreshold < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(goalStallThreshold));
        }
        lifecycleSession = _lifecycleRecorder?.Start(
            policy.Name,
            onlyGoalId,
            started,
            maxIterations,
            maxDuration);

        TimeSpan ConsumeWatchInterval(TimeSpan configuredInterval, int recheckableBlockedGoals)
        {
            var computedInterval = GetWatchFallbackInterval(kernel, onlyGoalId, configuredInterval);
            if (recheckableBlockedGoals <= 0)
            {
                return computedInterval;
            }

            var effectiveInterval = RetryLoopPolicy.ClampInterval(
                computedInterval,
                TimeSpan.FromSeconds(WatchStopPollIntervalSeconds));
            if (effectiveInterval == computedInterval)
            {
                return effectiveInterval;
            }

            foreach (var entry in setAsideGoals.Values.Where(entry =>
                         (onlyGoalId is null || entry.GoalId == onlyGoalId) &&
                         kernel.Goals.Any(goal =>
                             goal.Id.Value == entry.GoalId &&
                             !ConductorWorkingSet.IsTerminalGoal(goal))))
            {
                var key = new RetryDiagnosticKey(
                    "BLOCKED_RECHECK_INTERVAL_CLAMPED",
                    ShortGoalId(entry.GoalId),
                    entry.Condition.ToString().ToLowerInvariant());
                if (clampedRechecks.Add(key))
                {
                    EmitProgress(
                        $"BLOCKED_RECHECK_INTERVAL_CLAMPED goal={key.Goal} condition={key.Condition} computedSeconds={computedInterval.TotalSeconds:0.###} floorSeconds={WatchStopPollIntervalSeconds}");
                }
            }

            return effectiveInterval;
        }
        void HandleLandingRelaunch(int tick, ConductorLandingReceipt receipt, bool changedFilesAvailable = true)
        {
            _landingRelaunchDecisions.Handle(tick, receipt, () =>
            {
                selfRelaunchDrainCap.NoteLanding(receipt.ChangedFiles);
                pendingSelfRelaunch = new ConductorSelfRelaunchRequest(receipt.GoalId, tick);
                deferredSelfRelaunch = null;
                selfRelaunchRetryAfterTick = null;
                selfRelaunchDrainStartedAt ??= _utcNow();
            }, canaryDrain.Track, line => EmitProgress(line), changedFilesAvailable);
        }
        var initiallyCompletedGoalIds = GetCompletedGoalIds(kernel);
        if (observeTickWithoutDispatch is null)
        {
            _selectedExecutionBackstop ??= SelectedExecutionBackstopCoordinator.Create(_workspace, driver, _utcNow, line => EmitProgress(line));
            if (keepAliveWhenIdle && onlyGoalId is null)
                _mainFullVerification = MainFullVerificationCoordinator.Create(_workspace, driver, _utcNow, line => EmitProgress(line));
            if (_mainFullVerification is { } scheduledVerification)
                driver.ParallelAcceptanceAttemptCoordinator.GetMainFullVerificationRunId = () => scheduledVerification.ActiveRunId;
            driver.GateMissDetector ??= _selectedExecutionBackstop is { } backstop ? new(backstop, line => EmitProgress(line)) : null;
            driver.SuccessfulLandingSink = receipt =>
            {
                var landedAt = _promptRolloutWatch is null ? default : _utcNow();
                RecordSuccessfulLanding(landedGoalIds, receipt.GoalId);
                HandleLandingRelaunch(totalTicks, receipt);
                NoteLandingAndStopSupersededAttempts(kernel, driver, receipt.GoalId);
                _promptRolloutWatch?.NoteLanding(receipt, landedAt);
                previousSuccessfulLandingSink?.Invoke(receipt);
                _selectedExecutionBackstop?.NoteLanding(receipt, _workspace!.ExecutionDirectory);
            };
        }
        var workerAdmission = driver.GetWorkerAdmissionSnapshot(policy);
        _loadTrxCoherenceVerdicts(_workspace?.OrchestratorDirectory ?? leaseDirectory);
        EmitProgress(
            $"LOOP_START policy={Sanitize(policy.Name)} policySource={SanitizeReason(policySource)} maxIterations={maxIterations?.ToString() ?? "none"} " +
            $"maxDurationSeconds={(maxDuration.HasValue ? ((int)maxDuration.Value.TotalSeconds).ToString() : "none")} " +
            $"configuredWorkerCap={workerAdmission.ConfiguredWorkerCap} workerAdmissionCapacity={workerAdmission.AdmissionCapacity} " +
            $"reservedGateSlots={workerAdmission.ReservedGateSlots} effectiveWorkerCap={workerAdmission.EffectiveWorkerCap} " +
            $"acceptanceWidth={policy.AcceptanceWidth} adoptedBuildPushEnabled={policy.AdoptedBuildPushEnabled} buildRootReapIntervalSeconds={policy.BuildRootReapIntervalSeconds} {policy.Features.ToLogFields()} gitPath={SanitizeReason(ConductorGitResolution.Current.LogValue)}" +
            (string.IsNullOrWhiteSpace(journalMode) ? string.Empty : $" journalMode={Sanitize(journalMode)}"));
        SelfRelaunchDrainOutcome DrainSelfRelaunch(int tick)
        {
            if (pendingSelfRelaunch?.HomeBuildTarget is { } homeTarget)
            {
                // Follower reservations never enter the home drain-cap/detach path.
                var followerResult = _homeBuildRelaunchCoordinator!.TryHandoff(pendingSelfRelaunch, tick,
                    HomeBuildBlockers, canaryDrain.Wait);
                if (followerResult is not null)
                {
                    handedOffHomeBuild = homeTarget;
                    selfRelaunchHandoff = followerResult.Handoff;
                    EmitHandoffProgress(tick, followerResult.Handoff!, pendingSelfRelaunch.GoalId);
                    return new(SelfRelaunchDrainDisposition.BreakLoop);
                }
                pendingSelfRelaunch = null;
                canaryDrain.ResetAfterDeclinedHandoff();
                return new(SelfRelaunchDrainDisposition.Proceed);
            }
            var activeDispatches = RunJanitorialPhase<int?>(
                "count-running-dispatches",
                tick,
                () => CountRunningDispatches(kernel, onlyGoalId));
            if (!activeDispatches.HasValue)
            {
                return new(SelfRelaunchDrainDisposition.ContinueTick);
            }
            var liveGates = RunJanitorialPhase<int?>(
                "count-live-grouped-gates", tick, () => _countLiveGroupedGates()) ?? 0;
            var drainElapsed = _utcNow() - (selfRelaunchDrainStartedAt ?? _utcNow());
            var capDecision = new SelfRelaunchCapDecision(false, false, string.Empty);
            if (activeDispatches.Value > 0)
            {
                capDecision = selfRelaunchDrainCap.Evaluate(
                    kernel, onlyGoalId, excludedGoals, reapedGoals, drainElapsed);
                if (capDecision.Detach)
                {
                    var detachedBefore = CountGracefullyDetachedRunningDispatches(kernel, onlyGoalId);
                    var capReapedGoals = new HashSet<string>(reapedGoals, StringComparer.Ordinal);
                    DetachNonTerminalEligibleGoals(kernel, driver, onlyGoalId, excludedGoals, capReapedGoals);
                    selfRelaunchDrainCap.Detached = true;
                    var detached = CountGracefullyDetachedRunningDispatches(kernel, onlyGoalId) - detachedBefore;
                    EmitProgress($"LOOP_RELAUNCH_DETACH tick={totalTicks} goal={pendingSelfRelaunch!.GoalId} detached={detached} capMinutes={selfRelaunchDrainCap.Cap.TotalMinutes:0} admitting=false");
                    PersistGracefulDetachCheckpoint(
                        persistTick, persistGoalTick, kernel, totalTicks, onlyGoalId, "self-relaunch-detach", null,
                        busyWriteDelay, checkpointGoalTick, checkpointHeldGoals);
                }
            }

            var drainHold = new ConductorSelfRelaunchDrainHold(
                activeDispatches.Value > 0 && !capDecision.Detach && !capDecision.AlreadyDetached, liveGates, drainElapsed);
            if (drainHold.ShouldWait)
            {
                if (drainHold.TimedOut)
                {
                    EmitSelfRelaunchRollback(totalTicks, pendingSelfRelaunch!.GoalId, "drain", drainHold.TimeoutReason);
                    deferredSelfRelaunch = pendingSelfRelaunch;
                    selfRelaunchRetryAfterTick = totalTicks + 1;
                    pendingSelfRelaunch = null;
                    selfRelaunchDrainStartedAt = null;
                }
                if (pendingSelfRelaunch is not null)
                {
                    EmitProgress(drainHold.DescribeProgress(totalTicks, pendingSelfRelaunch.GoalId, activeDispatches.Value, capDecision.DrainSuffix));
                    TryPersistCheckpoint(
                        persistTick,
                        persistGoalTick,
                        kernel,
                        totalTicks,
                        onlyGoalId,
                        "self-relaunch-drain",
                        null,
                        busyWriteDelay,
                        checkpointGoalTick: checkpointGoalTick,
                        checkpointHeldGoalIds: checkpointHeldGoals);
                    drainHold.Wait(sleepFunc, interval => watchSleep.Wait(
                        interval, wakeSignal, GetRunningDispatchExitCodePaths(kernel, onlyGoalId), observeDrain: ObserveDrain));
                    return new(SelfRelaunchDrainDisposition.ContinueTick,
                        HeldForRunningDispatches: drainHold.DispatchesPending);
                }
            }

            if (pendingSelfRelaunch is null)
            {
                return new(SelfRelaunchDrainDisposition.Proceed);
            }

            var canaryAwaited = RunJanitorialPhase<bool?>("await-canary-tasks", tick, () =>
            {
                canaryDrain.Wait();
                return true;
            });
            if (canaryAwaited != true)
            {
                return new(SelfRelaunchDrainDisposition.ContinueTick);
            }

            EmitProgress(
                $"LOOP_RELAUNCH_REBUILD tick={totalTicks} goal={pendingSelfRelaunch.GoalId} active=0 admitting=false");
            ConductorSelfRelaunchResult relaunchResult;
            try
            {
                relaunchResult = _selfRelaunch!(pendingSelfRelaunch);
            }
            catch (Exception ex)
            {
                EmitProgress(
                    $"LOOP_HANDOFF_FAILED tick={totalTicks} goal={pendingSelfRelaunch.GoalId} phase=handoff " +
                    $"rolledBack=false continuing=false reason={SanitizeHandoffDetail($"{ex.GetType().Name}: {ex.Message}")}");
                return new(
                    SelfRelaunchDrainDisposition.Proceed,
                    new InvalidOperationException(
                        "Self-relaunch failed without confirming incumbent authority; refusing to continue the conductor loop.",
                        ex));
            }
            if (relaunchResult.HandedOff)
            {
                selfRelaunchHandoff = relaunchResult.Handoff;
                EmitHandoffProgress(totalTicks, relaunchResult.Handoff!, pendingSelfRelaunch.GoalId);
                return new(SelfRelaunchDrainDisposition.BreakLoop);
            }

            if (!relaunchResult.IncumbentCanContinue)
            {
                EmitProgress(
                    $"LOOP_HANDOFF_FAILED tick={totalTicks} goal={pendingSelfRelaunch.GoalId} phase=handoff " +
                    $"rolledBack=false continuing=false reason={SanitizeHandoffDetail(relaunchResult.Reason ?? "rollback authority was not confirmed")}");
                return new(
                    SelfRelaunchDrainDisposition.Proceed,
                    new InvalidOperationException(
                        "Self-relaunch rollback did not confirm incumbent authority; refusing to continue the conductor loop."));
            }

            if (string.Equals(relaunchResult.FailedPhase, "handoff", StringComparison.Ordinal))
            {
                EmitProgress(
                    $"LOOP_HANDOFF_FAILED tick={totalTicks} goal={pendingSelfRelaunch.GoalId} phase=handoff " +
                    $"rolledBack=true continuing=true reason={SanitizeHandoffDetail(relaunchResult.Reason ?? "unknown")}");
            }
            else
            {
                EmitSelfRelaunchRollback(
                    totalTicks,
                    pendingSelfRelaunch.GoalId,
                    relaunchResult.FailedPhase ?? "build",
                    relaunchResult.Reason ?? "unknown");
            }

            pendingSelfRelaunch = null;
            selfRelaunchDrainStartedAt = null;
            selfRelaunchDrainCap.Reset();
            canaryDrain.ResetAfterDeclinedHandoff();
            return new(SelfRelaunchDrainDisposition.Proceed);
        }

        TerminalGoalSweepResult? Sweep() =>
            _sweep(kernel, ProtectedOperatorIntentGoalIds(kernel, checkpointHeldGoals), !drain.IsDraining);

        void ReconcilePendingWakeMaintenance(int tick, List<string> timingLines)
        {
            if (pendingWakeMaintenanceKind is not { } kind) return;
            pendingWakeMaintenanceKind = null;
            RunJanitorialPhase(kind, tick, Sweep);
            RunJanitorialPhase(kind.Replace("-sweep", "-recover-interrupted-dispatches", StringComparison.Ordinal), tick, () =>
            {
                _recoverInterruptedDispatches(kernel);
                return true;
            });
            TryPersistCheckpoint(persistTick, persistGoalTick, kernel, tick, onlyGoalId,
                kind, timingLines, busyWriteDelay,
                checkpointGoalTick: checkpointGoalTick, checkpointHeldGoalIds: checkpointHeldGoals);
        }

        IReadOnlyList<string> HomeBuildBlockers() => ProjectConductorIdleCheck.ReadBlockers(
            () => CountRunningDispatches(kernel, onlyGoalId), driver, _countLiveGroupedGates,
            _operatorIntents, _homeBuildIdleBlockers);

        bool ObserveDrain()
        {
            if (!drain.Observe())
                return false;

            var inFlight = drain.Capture(kernel, driver, onlyGoalId);
            drain.Note(inFlight);
            EmitProgress(drain.FormatProgress(totalTicks, inFlight));
            return true;
        }

        bool TryFinishDrain()
        {
            var inFlight = drain.Capture(kernel, driver, onlyGoalId);
            drain.Note(inFlight);
            var reconciledSweepRemedies = ReconcileDrainedSweepRemedies(totalTicks);
            if (inFlight.IsEmpty && checkpointHeldGoals.Count == 0 && reconciledSweepRemedies)
            {
                stopRequested = true;
                StopLoop(ConductorDrainSignal.DrainedReason);
                return true;
            }

            if (!drain.IsExpired)
            {
                EmitProgress(drain.FormatProgress(totalTicks, inFlight));
                return false;
            }

            stopRequested = true;
            StopLoop(ConductorDrainSignal.TimeoutReason, drain.FormatTimeout(inFlight));
            DetachNonTerminalEligibleGoals(kernel, driver, onlyGoalId, excludedGoals, reapedGoals);
            PersistGracefulDetachCheckpoint(
                persistTick, persistGoalTick, kernel, totalTicks, onlyGoalId, ConductorDrainSignal.TimeoutReason, null,
                busyWriteDelay, checkpointGoalTick, checkpointHeldGoals);
            return true;
        }

        void PublishTick(BatchTickSummary summary, GoalSnapshotByteAccounting snapshotBytes, bool activationSweepFollows = false) =>
            ConductorTickPusher.Publish(summary, snapshotBytes, onTick, pendingReadWork: activationSweepFollows && _workspace is not null);

        while (true)
        {
            if (observeTickWithoutDispatch is not null)
            {
                if (watchSleep.IsStopRequested())
                {
                    stopRequested = true;
                    StopLoop("stop-file");
                    break;
                }
                if (maxIterations <= totalTicks)
                {
                    StopLoop("max-iter");
                    break;
                }
                if (maxDuration is { } limit && _utcNow() - started >= limit)
                {
                    StopLoop("max-duration");
                    break;
                }
                using (var observationBytes = GoalSnapshotByteAccounting.Begin())
                {
                    observeTickWithoutDispatch();
                    PublishTick(new BatchTickSummary(++totalTicks, 0, 0, 0, 0, 0, WatchSleeping: true), observationBytes);
                }
                if (watchSleep.Wait(watchInterval ?? TimeSpan.FromSeconds(1), null, []) != WatchSleepResult.StopRequested)
                    continue;
                stopRequested = true;
                StopLoop("stop-file-during-sleep");
                break;
            }
            using var writeOperationTag = SqliteOrchestratorStateRepository.UseWriteOperationTag("loop:tick");
            _tickPhaseElapsedMs.Clear();
            if (_mainFullVerification is { } mainVerification)
                RunJanitorialPhase("main-full-verification-poll", totalTicks + 1, () => { mainVerification.Poll(); return true; });
            if (pendingSelfRelaunch is null &&
                deferredSelfRelaunch is not null &&
                totalTicks >= selfRelaunchRetryAfterTick)
            {
                pendingSelfRelaunch = deferredSelfRelaunch;
                deferredSelfRelaunch = null;
                selfRelaunchRetryAfterTick = null;
                selfRelaunchDrainStartedAt = _utcNow();
            }

            if (watchSleep.IsStopRequested())
            {
                ReconcilePendingWakeMaintenance(totalTicks, []);
                stopRequested = true;
                StopLoop("stop-file");
                Console.WriteLine($"[conduct --loop] Stop signal detected at tick {totalTicks + 1}; no new dispatches will be started.");
                DetachNonTerminalEligibleGoals(kernel, driver, onlyGoalId, excludedGoals, reapedGoals);
                PersistGracefulDetachCheckpoint(
                    persistTick, persistGoalTick, kernel, totalTicks, onlyGoalId, "stop", null, busyWriteDelay,
                    checkpointGoalTick, checkpointHeldGoals);
                break;
            }

            ObserveDrain();

            if (!drain.IsDraining && pendingSelfRelaunch is null &&
                maxIterations.HasValue &&
                totalTicks >= maxIterations.Value)
            {
                ReconcilePendingWakeMaintenance(totalTicks, []);
                StopLoop("max-iter", $"max={maxIterations.Value}");
                Console.WriteLine($"[conduct --loop] Max iterations ({maxIterations.Value}) reached after {totalTicks} ticks.");
                DetachNonTerminalEligibleGoals(kernel, driver, onlyGoalId, excludedGoals, reapedGoals);
                PersistGracefulDetachCheckpoint(
                    persistTick, persistGoalTick, kernel, totalTicks, onlyGoalId, "max-iterations", null, busyWriteDelay,
                    checkpointGoalTick, checkpointHeldGoals);
                break;
            }

            if (!drain.IsDraining && pendingSelfRelaunch is null &&
                maxDuration.HasValue &&
                _utcNow() - started >= maxDuration.Value)
            {
                var now = _utcNow();
                maxDurationDeferralStartedAt ??= now;
                var snapshot = ConductorMaxDurationSnapshotBuilder.Build(kernel, driver, onlyGoalId, maxDurationDeferredAttemptIds);
                var verdict = ConductorMaxDurationStopDeferral.Decide(
                    now,
                    maxDurationDeferralStartedAt.Value,
                    snapshot.Attempts,
                    maxDurationDeferralCeiling ?? AcceptanceCheckTimeouts.DefaultTimeout,
                    snapshot.Failure);
                if (verdict.Kind == ConductorMaxDurationStopVerdictKind.Defer)
                {
                    isDeferringMaxDurationStop = true;
                    if (maxDurationDeferredAttemptIds is null && snapshot.Failure is null && verdict.Attempts.Count > 0)
                        maxDurationDeferredAttemptIds = verdict.Attempts.Select(attempt => attempt.AttemptId).ToHashSet(StringComparer.Ordinal);
                    onMaxDurationDeferralStateChanged?.Invoke(true);
                    if (!maxDurationDeferralAnnounced)
                        EmitProgress(ConductorMaxDurationStopDeferral.FormatDeferredEvent(totalTicks, now, maxDurationDeferralStartedAt.Value, maxDurationDeferralCeiling ?? AcceptanceCheckTimeouts.DefaultTimeout, verdict.Attempts, snapshot.Failure));
                    maxDurationDeferralAnnounced = true;
                }
                else
                {
                    ReconcilePendingWakeMaintenance(totalTicks, []);
                    onMaxDurationDeferralStateChanged?.Invoke(false);
                    StopLoop("max-duration", ConductorMaxDurationStopDeferral.FormatStopDetail(maxDuration.Value, verdict, snapshot.Failure));
                    Console.WriteLine($"[conduct --loop] Max duration ({maxDuration.Value.TotalSeconds:0}s) reached after {totalTicks} ticks.");
                    DetachNonTerminalEligibleGoals(kernel, driver, onlyGoalId, excludedGoals, reapedGoals);
                    PersistGracefulDetachCheckpoint(
                        persistTick, persistGoalTick, kernel, totalTicks, onlyGoalId, "max-duration", null, busyWriteDelay,
                        checkpointGoalTick, checkpointHeldGoals);
                    maxDurationReached = true;
                    break;
                }
            }

            var nextTick = totalTicks + 1;
            using var tickSnapshotBytes = GoalSnapshotByteAccounting.Begin();
            using var tickStepLedger = BeginTickCpuAndStepLedger();
            if (reloadPolicy is not null)
            {
                try
                {
                    var reloaded = reloadPolicy();
                    if (!PoliciesMatch(policy, reloaded.Policy) ||
                        !string.Equals(policySource, reloaded.Source, StringComparison.Ordinal))
                    {
                        foreach (var warning in reloaded.Warnings)
                        {
                            EmitProgress($"POLICY_WARNING tick={nextTick} message={SanitizeReason(warning)}");
                        }

                        EmitProgress(reloaded.FormatReload(nextTick, policy));
                        policy = reloaded.Policy;
                        policySource = reloaded.Source;
                    }
                }
                catch (Exception ex) when (ex is FormatException or IOException or UnauthorizedAccessException)
                {
                    EmitProgress($"POLICY_RELOAD_FAILED tick={nextTick} source={SanitizeReason(policySource)} currentPolicy={Sanitize(policy.Name)} reason={SanitizeReason($"{ex.GetType().Name}: {ex.Message}")}");
                    if (ex is FormatException)
                        EmitProgress($"POLICY_INVALID tick={nextTick} source={SanitizeReason(policySource)} currentPolicy={Sanitize(policy.Name)} reason={SanitizeReason(ex.Message)}");
                }
            }

            var preTickTimingLines = new List<string>();
            if (checkpointGoalTick is not null && checkpointHeldGoals.Count > 0)
            {
                var recoveryGoalIds = kernel.Goals
                    .Where(goal => checkpointHeldGoals.ContainsKey(goal.Id.Value))
                    .Select(goal => goal.Id)
                    .ToArray();
                if (recoveryGoalIds.Length > 0)
                {
                    var recoveredIntentGoalIds = ApplyCheckpointOutcomes(
                        checkpointGoalTick(kernel, recoveryGoalIds),
                        recoveryGoalIds,
                        checkpointHeldGoals,
                        nextTick,
                        "recovery",
                        preTickTimingLines,
                        deferEmission: true);
                    CompletePersistedOperatorIntents(recoveredIntentGoalIds, preTickTimingLines);
                }
            }
            var intentBoundaryClock = StartDiagnosticTimer();
            var intentBoundaryCpuStart = ReadProcessCpu();
            var ranIntentBoundaryAtTickStart = !drain.IsDraining && ConductorRelaunchDrainIntentBoundary.RunsAtTickStart(
                pendingSelfRelaunch is not null);
            var intentBoundary = ranIntentBoundaryAtTickStart
                ? ServiceOperatorIntentBoundary(kernel, driver, onlyGoalId, checkpointHeldGoals,
                    nextTick, persistTick, persistGoalTick, checkpointGoalTick, busyWriteDelay)
                : new ConductorOperatorIntentBoundary.Result();
            intentBoundary.ReadmitAppliedGoals(excludedGoals, setAsideGoals, selfClearedSetAsideEntries,
                escalatedGoals, completedGoals, reapedGoals, goalProjectionCache);
            intentBoundaryClock.Stop();
            if (_operatorIntents is not null)
                preTickTimingLines.Add(FormatPhaseTiming(nextTick, "operator-intent", intentBoundaryClock.Elapsed,
                    $"operator_intent_steps={_stepLedger!.FormatSteps(ConductorTickStepLedger.OperatorIntentNames)}",
                    cpuMs: CpuDeltaMs(intentBoundaryCpuStart, ReadProcessCpu())));
            ReconcilePendingWakeMaintenance(nextTick, preTickTimingLines);
            var preSweepBaseline = GoalKernelChange.CaptureAll(kernel);
            TerminalGoalJournalMetadataCache.BeginMeasurement();
            var sweepClock = StartDiagnosticTimer();
            var sweepCpuStart = ReadProcessCpu();
            var sweepResult = RunJanitorialPhase("sweep", nextTick, Sweep);
            foreach (var sweepEvent in (sweepResult?.Events ?? []).Concat(sweepResult?.OwnedRoots?.OperatorEvents ?? []))
            {
                EmitProgress(sweepEvent);
            }
            var sweepTerminalizedGoalIds = RunJanitorialPhase(
                "persist-sweep-terminalizations",
                nextTick,
                () => PersistSweepTerminalizations(
                    sweepResult, kernel, checkpointGoalTick, persistGoalTick, checkpointHeldGoals, nextTick, preTickTimingLines, busyWriteDelay, preSweepBaseline)) ?? [];
            foreach (var recovered in RecoverSweepLandingReceipts(sweepResult, sweepTerminalizedGoalIds))
                HandleLandingRelaunch(nextTick, recovered.Receipt, recovered.ChangedFilesAvailable);
            RunJanitorialPhase("recover-interrupted-dispatches", nextTick, () =>
            {
                _recoverInterruptedDispatches(kernel);
                return true;
            });
            // Reserve before any dispatch, gate acquisition, landing or next intent application.
            if (!drain.IsDraining && pendingSelfRelaunch is null && _selfRelaunchEnabled)
                pendingSelfRelaunch = _homeBuildRelaunchCoordinator?.TryReserve(nextTick, HomeBuildBlockers);
            if (!drain.IsDraining && pendingSelfRelaunch is not null)
            {
                var drainOutcome = RunJanitorialPhase(
                    "self-relaunch-drain",
                    nextTick,
                    () => DrainSelfRelaunch(nextTick));
                drainOutcome?.ThrowIfFaulted();
                if (drainOutcome is null || drainOutcome.Disposition == SelfRelaunchDrainDisposition.ContinueTick)
                {
                    if (ConductorRelaunchDrainIntentBoundary.RunsAfterDrainStep(
                        ranIntentBoundaryAtTickStart, drainOutcome, pendingSelfRelaunch is not null))
                    {
                        var drainIntentBoundary = ServiceOperatorIntentBoundary(
                            kernel, driver, onlyGoalId, checkpointHeldGoals, nextTick,
                            persistTick, persistGoalTick, checkpointGoalTick, busyWriteDelay);
                        drainIntentBoundary.ReadmitAppliedGoals(excludedGoals, setAsideGoals, selfClearedSetAsideEntries,
                            escalatedGoals, completedGoals, reapedGoals, goalProjectionCache);
                        foreach (var line in drainIntentBoundary.Lines) EmitProgress(line);
                    }
                    resolverObservation?.ObserveDrainPass(policy.PreTesterResolverObservationWindowId);
                    continue;
                }
                if (drainOutcome.Disposition == SelfRelaunchDrainDisposition.BreakLoop)
                {
                    resolverObservation?.ObserveDrainPass(policy.PreTesterResolverObservationWindowId);
                    break;
                }
            }
            using var resolverTick = resolverObservation?.BeginTick(policy.PreTesterResolverObservationWindowId, _utcNow);
            RunJanitorialPhase("readmit-resolved-set-aside-goals", nextTick, () =>
            {
                var decisions = ReadmitResolvedSetAsideGoals(
                    kernel, driver, sweepResult, onlyGoalId, setAsideGoals, selfClearedSetAsideEntries,
                    excludedGoals, escalatedGoals, reapedGoals, goalProjectionCache, _utcNow(), readmittedRetryReservations);
                ReadmitRecoveredSetAsideGoals(kernel, onlyGoalId, setAsideGoals, completedGoals,
                    escalatedGoals, dependencyEscalatedGoals, reapedGoals, advanceFaultRetries, goalProjectionCache);
                return GoalPolicyDecisionCheckpoint.Persist(kernel, decisions.Select(decision => decision.GoalId).ToArray(), decisions, checkpointGoalTick, (outcomes, ids) => ApplyCheckpointOutcomes(outcomes, ids, checkpointHeldGoals, nextTick, "set-aside-readmission", preTickTimingLines), line => EmitProgress(line, preTickTimingLines));
            });
            RunJanitorialPhase("mark-completed-dependency-goals", nextTick, () =>
            {
                MarkCompletedDependencyGoals(kernel, driver, onlyGoalId, completedGoals, goalProjectionCache);
                return true;
            });
            RunJanitorialPhase("reconcile-unscoped-dispatchable-goals", nextTick, () =>
            {
                ReconcileUnscopedDispatchableGoals(
                    kernel, driver, onlyGoalId, setAsideGoals, excludedGoals, escalatedGoals, reapedGoals,
                    completedGoals, unscopedDispatchableTicks, goalProjectionCache, unscopedStallTickThreshold, nextTick);
                return true;
            });
            sweepClock.Stop();
            var dependencyMetadataTiming = TerminalGoalJournalMetadataCache.CompleteMeasurement();
            preTickTimingLines.Add(FormatPhaseTiming(nextTick, "sweep", sweepClock.Elapsed,
                $"goals={kernel.Goals.Count} completed_dependencies={completedGoals.Count} set_aside={setAsideGoals.Count} dependency_metadata_ms={dependencyMetadataTiming.ElapsedMilliseconds} dependency_journals_read={dependencyMetadataTiming.JournalsRead}{FormatSweepCacheDetail(sweepResult)}{FormatSweepPhaseAttribution(_tickPhaseElapsedMs)}", cpuMs: EndCpuPhase(sweepCpuStart, ref _tickCpuSweepMs)));

            _promptRolloutWatch?.EvaluateTick(kernel);
            if (!drain.IsDraining)
                RunJanitorialPhase("main-suspect-release", nextTick,
                    () => ServiceMainSuspectRelease(driver, canaryDrain.Tasks, canaryDrain.Gate));
            var preWalkClock = StartDiagnosticTimer();
            var preWalkCpuStart = ReadProcessCpu();
            RunJanitorialPhase("retire-until-goal-lessons", nextTick, () => ConductorTickStepLedger.Measure("retire-until-goal-lessons", () => RetireUntilGoalLessons(kernel)));
            IReadOnlySet<GoalId> hostedChangedGoalIds = drain.IsDraining
                ? new HashSet<GoalId>()
                : ServiceStewardAndAuthor(kernel, onlyGoalId);
            if (intentBoundary.DeferredIntents.Count > 0)
            {
                // Maintenance owns interrupted-work and landing/evidence reconciliation. Retry
                // only deferred members of the original snapshot against its current working set.
                var reconciled = ServiceOperatorIntentBoundary(kernel, driver, onlyGoalId, checkpointHeldGoals,
                    nextTick, persistTick, persistGoalTick, checkpointGoalTick, busyWriteDelay,
                    intentBoundary.DeferredIntents);
                intentBoundary.IncludeAfterMaintenance(reconciled);
                reconciled.ReadmitAppliedGoals(excludedGoals, setAsideGoals, selfClearedSetAsideEntries,
                    escalatedGoals, completedGoals, reapedGoals, goalProjectionCache);
            }
            var actionableIntentGoalIds = intentBoundary.ActionableGoalIds;
            var preWalkIntentLines = intentBoundary.Lines;
            if (!drain.IsDraining)
                preWalkIntentLines.AddRange(ConductorTickStepLedger.Measure("workspace-intents", () => ServiceWorkspaceIntents(kernel)));
            var preWalkIntentProcessed = preWalkIntentLines.Count > 0;
            var intentsAwaitingReload = intentBoundary.AwaitingReload +
                ConductorOperatorIntentBoundary.ObserveDeferredArrivals(_operatorIntents, onlyGoalId,
                    actionableIntentGoalIds, checkpointHeldGoals);

            _slotContentionHolds.Retain(kernel.Goals.Select(goal => goal.Id));
            var heldReceiptGoals = kernel.Goals.Where(goal => (onlyGoalId is null || goal.Id.Value == onlyGoalId) &&
                !intentBoundary.UnavailableGoalIds.Contains(goal.Id.Value) && !checkpointHeldGoals.ContainsKey(goal.Id.Value)).ToArray();
            var pendingHeldRegates = RunJanitorialPhase("refresh-held-gate-receipts", nextTick,
                () => driver.RefreshHeldGateReceipts?.Invoke(heldReceiptGoals) ?? []);
            foreach (var id in pendingHeldRegates ?? [])
            {
                excludedGoals.Remove(id);
                setAsideGoals.Remove(id);
                escalatedGoals.Remove(id);
                goalProjectionCache.Invalidate(new GoalId(id));
            }
            hostedChangedGoalIds = RunJanitorialPhase("track-pull-request-deliveries", nextTick,
                () => ConductorPullRequestTracking.Service(driver.PullRequestDeliveryTracker, heldReceiptGoals,
                    hostedChangedGoalIds, excludedGoals, setAsideGoals, escalatedGoals, goalProjectionCache)) ?? hostedChangedGoalIds;
            ConductorGateAttention.ReconcileAll(driver, heldReceiptGoals,
                (phase, action) => RunJanitorialPhase(phase, nextTick, action));
            var scopedGoals = kernel.Goals
                .Where(g => (onlyGoalId is null || g.Id.Value == onlyGoalId)
                    && !intentBoundary.UnavailableGoalIds.Contains(g.Id.Value)
                    && !sweepTerminalizedGoalIds.Contains(g.Id)
                    && !checkpointHeldGoals.ContainsKey(g.Id.Value)
                    && (!ConductorOwnerQuestionHolds.ExcludesFromWalk(g.CurrentHold?.State) || actionableIntentGoalIds.Contains(g.Id.Value))
                    && (!excludedGoals.Contains(g.Id.Value) || actionableIntentGoalIds.Contains(g.Id.Value))
                    && (!setAsideGoals.ContainsKey(g.Id.Value) || actionableIntentGoalIds.Contains(g.Id.Value)))
                .ToArray();
            var verifiedGoalIdsAtTickStart = scopedGoals.Where(goal => goal.Status == GoalStatus.Verified)
                .Select(goal => goal.Id).ToHashSet();
            var preWalkIntentChangedGoalIds = new HashSet<GoalId>(hostedChangedGoalIds);
            preWalkIntentChangedGoalIds.UnionWith(intentBoundary.ChangedGoalIds);
            var unpersistedIntentGoalIds = hostedChangedGoalIds
                .Concat(intentBoundary.ChangedGoalIds.Except(intentBoundary.DurableGoalIds)).ToHashSet();

            var parkedExcludedCount = scopedGoals.Count(g => g.Status == GoalStatus.Parked);
            var terminalExcludedCount = scopedGoals.Count(ConductorWorkingSet.IsPreWalkExcludedTerminalGoal);
            var preWalkCandidates = scopedGoals
                .Where(g => !ConductorWorkingSet.IsPreWalkExcludedGoal(g))
                .ToArray();
            var eligible = preWalkCandidates
                .Where(g => !preWalkIntentChangedGoalIds.Contains(g.Id))
                .Where(g => ConductorTickStepLedger.Measure("eligibility", () => ConductorWorkingSet.IsLoopEligibleGoal(g, driver, goalProjectionCache)))
                .ToArray();
            ResetScopedGoalStallCounters(eligible, unscopedDispatchableTicks);
            preWalkClock.Stop();
            AddLedgerPhaseTimings(preTickTimingLines, nextTick, "prewalk", preWalkClock.Elapsed,
                $"scoped={scopedGoals.Length} candidates={preWalkCandidates.Length} eligible={eligible.Length} deferred_intent={preWalkIntentChangedGoalIds.Count} excluded_parked={parkedExcludedCount} excluded_terminal={terminalExcludedCount} cache_entries={goalProjectionCache.Count}", cpuMs: EndCpuPhase(preWalkCpuStart, ref _tickCpuPrewalkMs));

            var admissionClock = StartDiagnosticTimer();
            var admissionCpuStart = ReadProcessCpu();
            var admissionHold = new ConductorAcceptanceAdmissionHold(_workspace?.OrchestratorDirectory ?? leaseDirectory,
                _utcNow, line => EmitProgress(line), () =>
                { var active = ConductorMaxDurationSnapshotBuilder.Build(kernel, driver, null, null); return active.Failure is null ? active.Attempts.Count : -1; }, nextTick);
            var hasParkedAcceptanceToReconcile = ConductorTickStepLedger.Measure("admission-pre-batch", () => scopedGoals.Any(goal => goal.IsExplicitlyParked &&
                driver.ParallelAcceptanceAttemptCoordinator.GetUnreconciledAttempts([goal.Id.Value]).Count > 0));
            if (!drain.IsDraining && eligible.Length == 0 && !hasParkedAcceptanceToReconcile)
            {
                admissionHold.Acknowledge();
                RunJanitorialPhase("selected-execution-backstop-idle", nextTick, () =>
                {
                    _selectedExecutionBackstop?.ServiceAsync().GetAwaiter().GetResult();
                    return true;
                });
                if (_mainFullVerification is { } idleVerification &&
                    !admissionHold.Suppress(isDeferringMaxDurationStop, pendingSelfRelaunch is not null))
                    RunJanitorialPhase("main-full-verification-idle", nextTick, () => idleVerification.Service(0,
                        () => CanAdmitIdleMainVerification(driver, policy.AcceptanceWidth, nextTick)));
                if (preWalkIntentProcessed || preWalkIntentChangedGoalIds.Count > 0)
                {
                    totalTicks++;
                    var intentTickLines = new List<string>();
                    foreach (var line in preTickTimingLines.Concat(preWalkIntentLines))
                    {
                        EmitProgress(line, intentTickLines);
                    }

                    EmitProgress(
                        $"TICK_END tick={totalTicks} advanced=0 held={preWalkIntentChangedGoalIds.Count} escalated=0 done=0",
                        intentTickLines);
                    var intentStatePersisted = unpersistedIntentGoalIds.Count == 0;
                    if (unpersistedIntentGoalIds.Count > 0 && checkpointGoalTick is not null)
                    {
                        var requested = unpersistedIntentGoalIds.ToArray();
                        var durable = ApplyCheckpointOutcomes(
                            checkpointGoalTick(kernel, requested),
                            requested,
                            checkpointHeldGoals,
                            totalTicks,
                            "operator-intent",
                            intentTickLines);
                        intentStatePersisted = durable.Count == requested.Length;
                        if (durable.Count > 0)
                            CompletePersistedOperatorIntents(durable, intentTickLines);
                    }
                    else if (unpersistedIntentGoalIds.Count > 0 && persistGoalTick is not null)
                    {
                        PersistGoalTickOrThrow(
                            persistGoalTick,
                            kernel,
                            unpersistedIntentGoalIds.ToArray(),
                            totalTicks,
                            intentTickLines,
                            busyWriteDelay);
                        intentStatePersisted = true;
                    }
                    else if (unpersistedIntentGoalIds.Count > 0)
                    {
                        intentStatePersisted = TryPersistTick(
                            persistTick,
                            kernel,
                            totalTicks,
                            ResolveGoalContext(unpersistedIntentGoalIds, onlyGoalId),
                            "operator-intent",
                            intentTickLines,
                            busyWriteDelay);
                    }

                    if (checkpointGoalTick is null && intentStatePersisted && unpersistedIntentGoalIds.Count > 0)
                    {
                        CompletePersistedOperatorIntents(unpersistedIntentGoalIds, intentTickLines);
                    }

                    totalHeld += preWalkIntentChangedGoalIds.Count;
                    PublishTick(new BatchTickSummary(
                        totalTicks,
                        Advanced: 0,
                        Held: preWalkIntentChangedGoalIds.Count,
                        Escalated: 0,
                        Retried: 0,
                        Done: 0,
                        WatchSleeping: false)
                    {
                        ProgressLines = intentTickLines,
                        OperatorDispositions = buildOperatorDispositions?.Invoke(kernel) ?? []
                    }, tickSnapshotBytes, activationSweepFollows: true);
                    if (watchSleep.IsStopRequested())
                    {
                        stopRequested = true;
                        StopLoop("stop-after-operator-intent");
                        DetachNonTerminalEligibleGoals(kernel, driver, onlyGoalId, excludedGoals, reapedGoals);
                        PersistGracefulDetachCheckpoint(
                            persistTick,
                            persistGoalTick,
                            kernel,
                            totalTicks,
                            onlyGoalId,
                            "stop-after-operator-intent",
                            null,
                            busyWriteDelay,
                            checkpointGoalTick,
                            checkpointHeldGoals);
                        break;
                    }

                    ObserveDrain();
                    CompleteActivationTick(totalTicks);
                    resolverTick?.Complete();
                    if (drain.IsDraining)
                        continue;
                    if (!(keepAliveWhenIdle && watchInterval is not null))
                    {
                        continue;
                    }
                }

                // Daemon keep-alive polls an empty backlog. Independently, any set-aside non-terminal goal
                // must reach another sweep/recheck even for a non-daemon invocation; otherwise a transient
                // escalation is indistinguishable from terminal completion and only a manual relaunch can
                // recover it.
                var recheckableBlockedGoals = ConductorWorkingSet.CountRecheckableNonTerminalGoals(
                    kernel,
                    onlyGoalId,
                    setAsideGoals,
                    transientRecheckableGoalIds: checkpointHeldGoals.Keys.ToHashSet(StringComparer.Ordinal));
                var transientLoadRecheckPending = hasTransientLoadHold?.Invoke() == true;
                if ((keepAliveWhenIdle && watchInterval is not null) || recheckableBlockedGoals > 0 || transientLoadRecheckPending || intentsAwaitingReload > 0)
                {
                    if (recheckableBlockedGoals > 0 || transientLoadRecheckPending || intentsAwaitingReload > 0)
                    {
                        blockedRecheckCycles++;
                        totalBlockedRechecks++;
                        UpdateBlockedRecheckRecurrences(sweepResult, setAsideGoals, blockedRecheckRecurrences);
                        var now = _utcNow();
                        if (lastBlockedRecheckHeartbeatAt is null ||
                            now - lastBlockedRecheckHeartbeatAt.Value >= _blockedRecheckHeartbeatInterval)
                        {
                            EmitProgress(FormatBlockedRecheckHeartbeat(blockedRecheckRecurrences, totalBlockedRechecks));
                            lastBlockedRecheckHeartbeatAt = now;
                        }
                        var blockedRecheckBudget = maxIterations ??
                            (watchInterval is not null
                                ? null
                                : DefaultBlockedRecheckCycles);
                        var blockedRecheckBudgetUsed = maxIterations.HasValue
                            ? totalTicks + blockedRecheckCycles
                            : blockedRecheckCycles;
                        if (blockedRecheckBudget.HasValue &&
                            blockedRecheckBudgetUsed >= blockedRecheckBudget.Value &&
                            unscopedDispatchableTicks.Count == 0)
                        {
                            var exhaustedExplicitIterationBudget = maxIterations.HasValue;
                            var exhaustedStopReason = exhaustedExplicitIterationBudget
                                ? "max-iter"
                                : "blocked-recheck-exhausted";
                            StopLoop(exhaustedStopReason,
                                $"max={blockedRecheckBudget.Value} blockedRechecks={blockedRecheckCycles}");
                            Console.WriteLine(exhaustedExplicitIterationBudget
                                ? $"[conduct --loop] Max iterations ({blockedRecheckBudget.Value}) reached after {totalTicks} ticks and {blockedRecheckCycles} blocked rechecks."
                                : $"[conduct --loop] Blocked recheck budget ({blockedRecheckBudget.Value}) exhausted after {totalTicks} ticks and {blockedRecheckCycles} blocked rechecks.");
                            DetachNonTerminalEligibleGoals(kernel, driver, onlyGoalId, excludedGoals, reapedGoals);
                            PersistGracefulDetachCheckpoint(
                                persistTick,
                                persistGoalTick,
                                kernel,
                                totalTicks,
                                onlyGoalId,
                                exhaustedExplicitIterationBudget ? "max-iterations" : "blocked-recheck-exhausted",
                                null,
                                busyWriteDelay,
                                checkpointGoalTick,
                                checkpointHeldGoals);
                            break;
                        }
                    }

                    var configuredInterval = watchInterval ?? TimeSpan.FromSeconds(DefaultWatchIntervalSeconds);
                    var idleInterval = ConsumeWatchInterval(
                        configuredInterval,
                        Math.Max(recheckableBlockedGoals, transientLoadRecheckPending ? 1 : 0));
                    if (totalTicks < nextTick)
                        CompleteActivationTick(nextTick);
                    EmitProgress(
                        recheckableBlockedGoals > 0 || transientLoadRecheckPending
                            ? $"BLOCKED_RECHECK_SLEEP goals={recheckableBlockedGoals}" +
                              (transientLoadRecheckPending ? " loadHeld=true" : string.Empty) +
                              $" seconds={(int)idleInterval.TotalSeconds}"
                            : $"IDLE_SLEEP seconds={(int)idleInterval.TotalSeconds}");
                    resolverTick?.Complete();
                    var idleSleep = sleepFunc is not null
                        ? (sleepFunc(idleInterval) ? WatchSleepResult.StopRequested : WatchSleepResult.FallbackElapsed)
                        : watchSleep.Wait(idleInterval, wakeSignal, GetRunningDispatchExitCodePaths(kernel, onlyGoalId), GetRunningAttemptExitCodePaths(driver, kernel, onlyGoalId), ObserveDrain);
                    if (idleSleep == WatchSleepResult.WakeSignaled)
                    {
                        pendingWakeMaintenanceKind = "idle-wake-sweep";
                    }

                    if (idleSleep == WatchSleepResult.StopRequested || watchSleep.IsStopRequested())
                    {
                        // A wake still owns exit reconciliation even when shutdown wins admission.
                        // Do not capture or apply another intent batch on this stopping path.
                        ReconcilePendingWakeMaintenance(nextTick, preTickTimingLines);
                        stopRequested = true;
                        StopLoop("stop-while-idle");
                        DetachNonTerminalEligibleGoals(kernel, driver, onlyGoalId, excludedGoals, reapedGoals);
                        PersistGracefulDetachCheckpoint(
                            persistTick, persistGoalTick, kernel, totalTicks, onlyGoalId, "stop-while-idle", null, busyWriteDelay,
                            checkpointGoalTick, checkpointHeldGoals);
                        break;
                    }

                    ObserveDrain();
                    continue;
                }

                var remainingNonTerminalGoals = kernel.Goals.Count(goal =>
                    (onlyGoalId is null || goal.Id.Value == onlyGoalId) &&
                    !ConductorWorkingSet.IsTerminalGoal(goal));
                foreach (var line in preTickTimingLines)
                {
                    EmitProgress(line);
                }
                StopLoop(
                    remainingNonTerminalGoals == 0 ? "all-terminal" : "no-recheckable-work",
                    remainingNonTerminalGoals == 0 ? null : $"nonTerminalGoals={remainingNonTerminalGoals}");
                Console.WriteLine($"[conduct --loop] All goals done or escalated; loop complete after {totalTicks} ticks.");
                break;
            }
            blockedRecheckCycles = 0;
            totalTicks++;
            var tickLines = new List<string>();
            ConductorTickStepLedger.Measure("admission-pre-batch", () =>
            {
                foreach (var line in preTickTimingLines)
                {
                    EmitProgress(line, tickLines);
                }
                foreach (var line in preWalkIntentLines)
                {
                    EmitProgress(line, tickLines);
                }
            });
            var changedGoalLines = new List<string>();
            var goalProgressFields = new Dictionary<string, GoalProgressEventFields?>();
            var changedGoalIds = ConductorTickStepLedger.Measure("admission-pre-batch", () => new HashSet<GoalId>(unpersistedIntentGoalIds.Concat(GoalKernelChange.ChangedSince(kernel, sweepResult?.ReloadBaseline ?? preSweepBaseline, checkpointHeldGoals.Keys, sweepTerminalizedGoalIds)).Concat(GoalEffectivePriorityIndex.ApplyPending(kernel.Goals, line => EmitProgress(line, tickLines), _utcNow, _priorityStarvationGuard.BeginTick(kernel, totalTicks), recordDecision: kernel.RecordGoalPolicyDecision))));
            var liveChangeSnapshots = new Dictionary<(string Worktree, string? BaseCommit), DispatchLiveChangeSnapshot>();
            DispatchLiveChangeSnapshot LiveChangesFor(TaskSpec task)
            {
                var dispatch = task.LastDispatch!;
                var key = (dispatch.WorkingDirectory, dispatch.BaseCommit);
                if (!liveChangeSnapshots.TryGetValue(key, out var snapshot))
                {
                    snapshot = GoalChangesReader.BuildLiveDispatchSnapshot(
                        dispatch.WorkingDirectory,
                        dispatch.BaseCommit,
                        displayLimit: 3);
                    liveChangeSnapshots[key] = snapshot;
                }

                return snapshot;
            }
            var tickAdvanced = 0;
            var tickHeld = 0;
            var tickEscalated = 0;
            var tickRetried = 0;
            var tickDone = 0;
            var dispatchRecordWriteSkippedGoals = new HashSet<string>(StringComparer.Ordinal);
            var dispatchRecordWriteBoundGoalIds = new HashSet<GoalId>();
            bool SuppressNewAcceptanceAdmission() =>
                admissionHold.Suppress(isDeferringMaxDurationStop, pendingSelfRelaunch is not null);
            ConductorTickStepLedger.Measure("begin-tick", () => driver.BeginTick(kernel, totalTicks));
            var drainGoals = drain.IsDraining ? kernel.Goals.Where(goal =>
                (onlyGoalId is null || goal.Id.Value == onlyGoalId) &&
                !checkpointHeldGoals.ContainsKey(goal.Id.Value)).ToArray() : [];
            var parallelLandingResults = drain.IsDraining
                ? ReconcileDrainingAcceptance(drainGoals, kernel, driver, policy, totalTicks, changedGoalLines, changedGoalIds)
                : RunParallelAcceptanceBatch(
                eligible,
                scopedGoals,
                verifiedGoalIdsAtTickStart,
                preWalkIntentChangedGoalIds,
                kernel,
                driver,
                policy,
                completedGoals,
                escalatedGoals,
                totalTicks,
                changedGoalLines,
                changedGoalIds,
                suppressNewAcceptanceAdmission: SuppressNewAcceptanceAdmission);
            admissionHold.Acknowledge();
            var previousPhaseTimingSink = driver.PhaseTimingSink;
            var perGoalPhaseTimingLines = new List<string>();
            driver.PhaseTimingSink = line => perGoalPhaseTimingLines.Add($"PHASE_TIMING tick={totalTicks} {line}");
            var goalWalkTimings = new List<GoalWalkTiming>();
            // Batch reconciliation needs tick context; its admission observations must not leak into the walk.
            ConductorTickStepLedger.Measure("begin-tick", () => driver.BeginTick(kernel, totalTicks));
            admissionClock.Stop();
            EmitProgress(FormatPhaseTiming(totalTicks, "acceptance-admission", admissionClock.Elapsed,
                _stepLedger!.FormatAdmission(admissionClock.Elapsed), EndCpuPhase(admissionCpuStart, ref _tickCpuAdmissionMs)), tickLines);
            var goalWalkClock = StartDiagnosticTimer();
            var goalWalkCpuStart = ReadProcessCpu();
            var glanceDurationStats = drain.IsDraining || _progressiveReviewGlances is null
                ? Array.Empty<TaskDurationStatsRecord>()
                : kernel.BuildTaskDurationStats();
            var walkOrder = drain.IsDraining ? Array.Empty<Goal>() : GoalWalkOrder.Order(eligible);
            if (drain.IsDraining)
            {
                foreach (var goal in drainGoals.Where(goal => goal.Tasks.Any(ConductorDrainSignal.DispatchNeedsReconciliation)))
                {
                    var beforeRefresh = GoalKernelChange.Capture(goal);
                    _refreshGoalDispatchesBeforeAdvance(kernel, goal);
                    if (GoalKernelChange.Changed(beforeRefresh, kernel, goal.Id))
                        changedGoalIds.Add(goal.Id);
                }
                drain.ReconcileFocusedEvidence(driver, drainGoals, totalTicks, line => EmitProgress(line, tickLines));
            }
            foreach (var goal in walkOrder)
            {
                if (isDeferringMaxDurationStop &&
                    !parallelLandingResults.ContainsKey(goal.Id.Value))
                {
                    _refreshGoalDispatchesBeforeAdvance(kernel, goal);
                    continue;
                }
                if (pendingSelfRelaunch is not null &&
                    !parallelLandingResults.ContainsKey(goal.Id.Value))
                {
                    break;
                }

                var label = goal.Id.Value[..8];
                var singleGoalClock = StartDiagnosticTimer();
                using var goalWalkScope = ConductorTickStepLedger.OpenScope("walk-goal-overhead");
                void FinishGoalWalk(string result)
                {
                    if (!singleGoalClock.IsRunning)
                    {
                        return;
                    }

                    singleGoalClock.Stop();
                    goalWalkTimings.Add(new GoalWalkTiming(label, result, singleGoalClock.Elapsed));
                }

                if (TrySkipGoalLeftWorkingSet(kernel, goal, totalTicks, "walk-start", tickLines, FinishGoalWalk)) continue;
                // Dependency ordering is a dispatch-start gate. Do not interrupt a worker that is
                // currently in flight, but re-evaluate the edge before any later dispatch starts.
                var dependencyRequiresPerson = true;
                var depHoldReason = HasStartedGoalWork(goal)
                    ? null
                    : ConductorDependencyEdgeReconciler.Evaluate(goal, completedGoals, escalatedGoals, kernel, out dependencyRequiresPerson, dependencyEscalatedGoals, changedGoalIds);
                if (depHoldReason is not null)
                {
                    var progressLine = $"GOAL goal={label} result=held reason={SanitizeReason(depHoldReason)}";
                    if (ConductorTickStepLedger.Measure("walk-disposition", () => RecordChangedDisposition(goal.Id.Value, progressLine, lastGoalDisposition, changedGoalLines)))
                    {
                        changedGoalIds.Add(goal.Id);
                        Console.WriteLine($"[conduct --loop] Tick {totalTicks}: {label} [{policy.Name}] → held: {depHoldReason}");
                        ConductorTickStepLedger.Measure("walk-policy-decision", () => kernel.RecordGoalPolicyDecision(goal.Id, $"Batch loop tick {totalTicks}: held: {depHoldReason}"));
                    }
                    // A goal held due to a failed/escalated dependency will never unblock unless
                    // future condition-specific re-entry logic says otherwise.
                    if (depHoldReason.StartsWith("dependency escalated", StringComparison.Ordinal) ||
                        depHoldReason.StartsWith("dependency-terminal-without-landing", StringComparison.Ordinal))
                    {
                        ClearGoalHold(kernel, goal, changedGoalIds);
                        escalatedGoals.Add(goal.Id.Value);
                        ConductorTickStepLedger.Measure("walk-reap", () => ReapGoalOnce(kernel, goal, reapedGoals, totalTicks, tickLines));
                        ConductorTickStepLedger.Measure("walk-set-aside", () => SetAsideDependencyEscalated(kernel, driver, goal, depHoldReason, setAsideGoals, selfClearedSetAsideEntries, dependencyEscalatedGoals));
                        tickEscalated++;
                    }
                    else
                    {
                        if (dependencyRequiresPerson)
                        {
                            TrackGoalHold(
                                kernel,
                                goal,
                                TryResolveLifecycleState(goalProjectionCache, driver, goal),
                                depHoldReason,
                                _utcNow(),
                                effectiveGoalStallThreshold,
                                changedGoalIds,
                                tickLines, driver: driver);
                        }
                        else
                        {
                            ClearGoalHold(kernel, goal, changedGoalIds);
                        }
                        tickHeld++;
                    }

                    FinishGoalWalk("dependency-held");
                    continue;
                }

                if (_invalidatedAttemptStopPolicy.TryHold(driver.ParallelAcceptanceAttemptCoordinator, goal.Id.Value,
                        InvalidatedAttemptStop, line => EmitProgress(line, tickLines), out var holdReason))
                {
                    var progressLine = $"GOAL goal={label} result=held reason={SanitizeReason(holdReason)}";
                    if (ConductorTickStepLedger.Measure("walk-disposition", () => RecordChangedDisposition(goal.Id.Value, progressLine, lastGoalDisposition, changedGoalLines)))
                    {
                        changedGoalIds.Add(goal.Id);
                        Console.WriteLine($"[conduct --loop] Tick {totalTicks}: {label} [{policy.Name}] → held: {holdReason}");
                        ConductorTickStepLedger.Measure("walk-policy-decision", () => kernel.RecordGoalPolicyDecision(goal.Id, $"Batch loop tick {totalTicks}: held: {holdReason}"));
                    }

                    tickHeld++;
                    TrackGoalHold(
                        kernel,
                        goal,
                        TryResolveLifecycleState(goalProjectionCache, driver, goal),
                        holdReason,
                        _utcNow(),
                        effectiveGoalStallThreshold,
                        changedGoalIds,
                        tickLines, driver: driver);
                    FinishGoalWalk("acceptance-cancellation-pending");
                    continue;
                }

                if (VerifiedAcceptanceEscalationDecision.HasUnresolvedPersistedVerifiedAcceptanceEscalation(goal, driver))
                {
                    var progressLine = $"GOAL goal={label} result=escalated state={GoalLifecycleState.Verified}";
                    if (ConductorTickStepLedger.Measure("walk-disposition", () => RecordChangedDisposition(goal.Id.Value, progressLine, lastGoalDisposition, changedGoalLines)))
                    {
                        changedGoalIds.Add(goal.Id);
                        Console.WriteLine($"[conduct --loop] Tick {totalTicks}: {label} [{policy.Name}] → escalated at Verified — acceptance verification already requires operator action.");
                    }

                    escalatedGoals.Add(goal.Id.Value);
                    ClearGoalHold(kernel, goal, changedGoalIds);
                    ConductorTickStepLedger.Measure("walk-reap", () => ReapGoalOnce(kernel, goal, reapedGoals, totalTicks, tickLines));
                    ConductorTickStepLedger.Measure("walk-set-aside", () => SetAside(kernel, driver, goal, BatchSetAsideCondition.LifecycleEscalation, setAsideGoals, selfClearedSetAsideEntries, sweepResult));
                    tickEscalated++;
                    FinishGoalWalk("verified-escalation");
                    continue;
                }

                var beforeAdvance = GoalKernelChange.Capture(goal);
                ConductorAdvanceResult result;
                ParallelLandingOutcome? parallelLandingOutcome = null;
                if (parallelLandingResults.TryGetValue(goal.Id.Value, out parallelLandingOutcome))
                {
                    result = parallelLandingOutcome.Result;
                }
                else
                {
                    if (!TryAdvanceGoal(
                        () => ConductorTickStepLedger.Measure<ConductorAdvanceResult?>("walk-held-goal", () =>
                        {
                            var beforeRefresh = GoalProjectionCache.BuildFingerprint(goal);
                            var runningHoldReason = ConductorTickStepLedger.Measure("walk-running-refresh", () => DetachedDispatchHoldReasonBuilder.Build(goal, _refreshGoalDispatchesBeforeAdvance(kernel, goal)));
                            if (_progressiveReviewGlances is not null && watchInterval is not null)
                            {
                                var glanceResult = ConductorTickStepLedger.Measure("walk-running-glance", () => _progressiveReviewGlances.Observe(
                                    kernel,
                                    [goal],
                                    glanceDurationStats,
                                    LiveChangesFor));
                                foreach (var line in glanceResult.ProgressLines)
                                {
                                    EmitProgress(line, tickLines);
                                }

                                if (glanceResult.MutatedTaskState)
                                {
                                    changedGoalIds.Add(goal.Id);
                                }
                            }

                            if (_progressiveReviewSteering?.CanExecutePending(goal, driver, watchInterval) == true)
                            {
                                if (driver.EnvironmentGate?.Check(goal, policy, GoalLifecycleState.Running) is { } environmentHold)
                                    return environmentHold;

                                var steerResult = _progressiveReviewSteering.ExecutePending(kernel, goal, policy);
                                foreach (var line in steerResult.ProgressLines)
                                {
                                    EmitProgress(line, tickLines);
                                }

                                if (steerResult.MutatedTaskState)
                                {
                                    changedGoalIds.Add(goal.Id);
                                    kernel.ClearGoalHold(goal.Id);
                                    tickHeld++;
                                    ConductorTickStepLedger.Measure("walk-projection-invalidate", () => goalProjectionCache.Invalidate(goal.Id));
                                    FinishGoalWalk("progressive-review-steer");
                                    return null;
                                }
                            }

                            ConductorTickStepLedger.Measure("walk-projection-invalidate", () => goalProjectionCache.Invalidate(goal.Id));
                            var afterRefresh = GoalProjectionCache.BuildFingerprint(goal);
                            if (!string.Equals(beforeRefresh, afterRefresh, StringComparison.Ordinal))
                            {
                                changedGoalIds.Add(goal.Id);
                            }

                            if (goal.Status == GoalStatus.Verified && admissionHold.FleetHeld())
                                return ParallelAcceptanceHeld(goal, policy, "Fleet restart is draining acceptance.");
                            var engineHealth = _acceptanceEngineCircuit?.Read();
                            return IsAcceptanceEngineCircuitHoldRequired(goal.Status, engineHealth)
                                ? ParallelAcceptanceHeld(
                                    goal,
                                    policy,
                                    BuildAcceptanceEngineHoldReason(engineHealth!))
                                : ConductorTickStepLedger.Measure("walk-advance", () => driver.AdvanceOnce(goal, policy, runningHoldReason));
                        }),
                        kernel,
                        driver,
                        goal,
                        policy,
                        totalTicks,
                        label,
                        changedGoalLines,
                        lastGoalDisposition,
                        changedGoalIds,
                        escalatedGoals,
                        reapedGoals,
                        setAsideGoals,
                        selfClearedSetAsideEntries,
                        dispatchRecordWriteSkips,
                        dispatchRecordWriteSkippedGoals,
                        dispatchRecordWriteBoundGoalIds,
                        tickLines,
                        ref tickHeld,
                        ref tickEscalated,
                        FinishGoalWalk,
                        out result))
                    {
                        if (GoalKernelChange.Changed(beforeAdvance, kernel, goal.Id)) changedGoalIds.Add(goal.Id);
                        continue;
                    }
                }

                // Auto-retry transient acceptance verification failures (up to maxVerifyRetries re-verifications)
                var serialRetryRan = false;
                if (!isDeferringMaxDurationStop && result.WasEscalated && VerifiedAcceptanceEscalationDecision.IsTransientVerificationFailure(result))
                {
                    retryCounts.TryGetValue(goal.Id.Value, out var retries);
                    var retryAdvanceFaulted = false;
                    while (retries < maxVerifyRetries && result.WasEscalated && VerifiedAcceptanceEscalationDecision.IsTransientVerificationFailure(result))
                    {
                        var nextRetry = retries + 1;
                        if (!TryAdvanceGoal(
                            () =>
                            {
                                var retryResult = ConductorTickStepLedger.Measure("walk-retry-advance", () => driver.AdvanceOnce(goal, policy));
                                serialRetryRan = true;
                                retries = nextRetry;
                                retryCounts[goal.Id.Value] = retries;
                                tickRetried++;
                                changedGoalLines.Add($"GOAL goal={label} result=retry attempt={retries}/{maxVerifyRetries}");
                                lastGoalDisposition[goal.Id.Value] = changedGoalLines[^1];
                                changedGoalIds.Add(goal.Id);
                                Console.WriteLine($"[conduct --loop] Tick {totalTicks}: {goal.Id.Value[..8]} acceptance flake (retry {retries}/{maxVerifyRetries})");
                                ConductorTickStepLedger.Measure("walk-policy-decision", () => kernel.RecordGoalPolicyDecision(goal.Id, $"Batch loop auto-retry acceptance verification (attempt {retries}/{maxVerifyRetries})"));
                                ConductorTickStepLedger.Measure("walk-projection-invalidate", () => goalProjectionCache.Invalidate(goal.Id));
                                return retryResult;
                            },
                            kernel,
                            driver,
                            goal,
                            policy,
                            totalTicks,
                            label,
                            changedGoalLines,
                            lastGoalDisposition,
                            changedGoalIds,
                            escalatedGoals,
                            reapedGoals,
                            setAsideGoals,
                            selfClearedSetAsideEntries,
                            dispatchRecordWriteSkips,
                            dispatchRecordWriteSkippedGoals,
                            dispatchRecordWriteBoundGoalIds,
                            tickLines,
                            ref tickHeld,
                            ref tickEscalated,
                            FinishGoalWalk,
                            out result))
                        {
                            if (GoalKernelChange.Changed(beforeAdvance, kernel, goal.Id)) changedGoalIds.Add(goal.Id);
                            retryAdvanceFaulted = true;
                            break;
                        }
                    }

                    if (retryAdvanceFaulted)
                        continue;
                }

                if (TrySkipGoalLeftWorkingSet(kernel, goal, totalTicks, "post-advance", tickLines, FinishGoalWalk)) continue;

                if (GoalKernelChange.Changed(beforeAdvance, kernel, goal.Id)) changedGoalIds.Add(goal.Id);
                if (TryReconcileAwaitingVerificationHold(kernel, goal, result, totalTicks, out var reconciledOutcome))
                {
                    changedGoalIds.Add(goal.Id);
                    result = result with { Outcome = reconciledOutcome };
                    ConductorTickStepLedger.Measure("walk-projection-invalidate", () => goalProjectionCache.Invalidate(goal.Id));
                }

                if (!AdmissionFactsPendingAdmission.IsPending(result)) ConductorTickStepLedger.Measure("walk-outcome-capacity", () => TrackGoalOutcomeAndCapacity(
                    kernel,
                    driver,
                    goal,
                    result.Outcome,
                    _utcNow(),
                    effectiveGoalStallThreshold,
                    changedGoalIds,
                    tickLines));

                var goalProgressLine = FormatGoalProgressLine(
                    label,
                    result.Outcome,
                    serialRetryRan ? null : parallelLandingOutcome?.SlotIndex);
                goalProgressFields[goalProgressLine] = GoalProgressEventFields.From(goal.Id.Value,
                    result.Outcome, serialRetryRan ? null : parallelLandingOutcome?.SlotIndex);
                if (!AdmissionFactsPendingAdmission.IsPending(result) && ConductorTickStepLedger.Measure("walk-disposition", () => RecordChangedDisposition(
                    goal.Id.Value,
                    goalProgressLine,
                    lastGoalDisposition,
                    changedGoalLines,
                    ShouldAlwaysEmitDisposition(result.Outcome),
                    emitUnchanged: PassedCohortReceiptApprovalHold.IsReadOnlyReservation(result))))
                {
                    // Changed held advances are already included by the before/after comparison.
                    if (!result.IsHeld)
                        changedGoalIds.Add(goal.Id);
                    Console.WriteLine($"[conduct --loop] Tick {totalTicks}: {label} [{policy.Name}] → {ConductorOutcomeText.Format(result.Outcome)}");
                    ConductorTickStepLedger.Measure("walk-policy-decision", () => kernel.RecordGoalPolicyDecision(goal.Id, $"Batch loop tick {totalTicks}: {ConductorOutcomeText.Format(result.Outcome)}",
                        VerifiedAcceptanceEscalationDecision.BuildTickOutcomePayload(result.Outcome)));
                }

                if (result.WasExecuted)        { tickAdvanced++; }
                else if (result.IsHeld)        { tickHeld++; }
                else if (result.WasEscalated)  { tickEscalated++; escalatedGoals.Add(goal.Id.Value); ConductorTickStepLedger.Measure("walk-reap", () => ReapGoalOnce(kernel, goal, reapedGoals, totalTicks, tickLines)); ConductorTickStepLedger.Measure("walk-set-aside", () => SetAside(kernel, driver, goal, GetSetAsideCondition(result), setAsideGoals, selfClearedSetAsideEntries, sweepResult)); }
                else if (result.IsDone)        { tickDone++;      completedGoals.Add(goal.Id.Value); excludedGoals.Add(goal.Id.Value); }
                ConductorTickStepLedger.Measure("walk-projection-invalidate", () => goalProjectionCache.Invalidate(goal.Id));
                FinishGoalWalk(result.Outcome.GetType().Name);
            }
            goalWalkClock.Stop(); var goalWalkOwnerCpu = ReadProcessCpu() - goalWalkCpuStart; ObserveWorkerCapacityTick(kernel, effectiveGoalStallThreshold);
            ConductorExperimentWatch.For(this, _workspace, CurrentConductEventLogWriter.Value ?? _conductEventLogWriter)?.ObserveTick(_utcNow());
            ConductorProviderHoldWatch.For(this, CurrentConductEventLogWriter.Value ?? _conductEventLogWriter)?.ObserveTick(kernel.ProviderHoldScope, _utcNow());
            // Admission runs on this thread. A failed gate read must defer the probe like a live gate.
            if (_providerHoldProbe is not null && RunJanitorialPhase<int?>(
                    "count-provider-hold-probe-gates", totalTicks, () => _countLiveGroupedGates()) == 0)
            {
                foreach (var id in _providerHoldProbe(kernel, _utcNow())) changedGoalIds.Add(id);
                ConductorProviderHoldWatch.For(this, CurrentConductEventLogWriter.Value ?? _conductEventLogWriter)?.ObserveTick(kernel.ProviderHoldScope, _utcNow());
            }
            driver.PhaseTimingSink = previousPhaseTimingSink;
            foreach (var line in perGoalPhaseTimingLines)
            {
                EmitProgress(line, tickLines);
            }

            EmitProgress(FormatPhaseTiming(totalTicks, "per-goal-walk", goalWalkClock.Elapsed,
                $"goals={goalWalkTimings.Count} slowest={FormatSlowestGoalWalks(goalWalkTimings)} {FormatWalkSteps(goalWalkClock.Elapsed, goalWalkOwnerCpu)}", cpuMs: EndCpuPhase(goalWalkCpuStart, ref _tickCpuWalkMs)), tickLines);

            totalAdvanced  += tickAdvanced;
            totalHeld      += tickHeld;
            totalEscalated += tickEscalated;
            totalRetried   += tickRetried;
            totalDone      += tickDone;

            var emitTickSummary = ShouldEmitTickSummary(changedGoalLines.Count, parkedExcludedCount, totalTicks);
            if (watchInterval is not null)
            {
                EmitWatchProgress(eligible, quiet, policy, watchInterval, stallWarningThreshold,
                    liveChangeSnapshots, totalTicks, tickLines);
            }

            if (emitTickSummary)
            {
                EmitProgress($"TICK tick={totalTicks} eligible={eligible.Length}", tickLines);
                foreach (var line in changedGoalLines)
                {
                    EmitProgress(line, tickLines, goalProgressFields.GetValueOrDefault(line));
                }

                if (parkedExcludedCount > 0)
                {
                    EmitProgress($"TICK_EXCLUDED tick={totalTicks} kind=parked count={parkedExcludedCount}", tickLines);
                }

                var summaryPrefix = changedGoalLines.Count > 0 ? "TICK_END" : "TICK_SUMMARY";
                EmitProgress($"{summaryPrefix} tick={totalTicks} advanced={tickAdvanced} held={tickHeld} escalated={tickEscalated} done={tickDone}{FormatTickCpuSummary()}", tickLines);
                Console.WriteLine($"[conduct --loop] Tick {totalTicks} summary: advanced={tickAdvanced} held={tickHeld} escalated={tickEscalated} retried={tickRetried} done={tickDone}");
            }

            AppendTickCpuLedger(totalTicks, changedGoalLines.Count > 0);
            CaptureLandingTickChanges(changedGoalIds, pendingSelfRelaunch is not null);
            // Durably checkpoint this tick's progress (dispatches started, reconcile results, escalations).
            // Without this the loop's mutations live only in memory until the whole command returns, so a
            // long-running watch loop never persists and a killed loop loses every dispatch on rollback —
            // the goal then re-dispatches the same stage forever and can never advance.
            // When persistGoalTick is supplied, persist only the goals whose disposition changed this tick
            // in one bulk checkpoint. The per-goal loop above is justified because it runs each goal's
            // state machine; the durable write is intentionally batched. A contention-bound escalation is
            // persisted separately so the same BUSY/LOCKED condition remains a per-goal degradation instead
            // of being promoted back into a loop-fatal end-of-tick write.
            if (checkpointGoalTick is not null)
            {
                var criticalGoalIds = changedGoalIds
                    .Except(dispatchRecordWriteBoundGoalIds)
                    .ToArray();
                if (criticalGoalIds.Length > 0)
                {
                    var durable = ApplyCheckpointOutcomes(
                        checkpointGoalTick(kernel, criticalGoalIds),
                        criticalGoalIds,
                        checkpointHeldGoals,
                        totalTicks,
                        "goal",
                        tickLines);
                    if (durable.Count > 0)
                        CompletePersistedOperatorIntents(durable, tickLines);
                }

                if (dispatchRecordWriteBoundGoalIds.Count > 0)
                {
                    var boundGoalIds = dispatchRecordWriteBoundGoalIds.ToArray();
                    var durable = ApplyCheckpointOutcomes(
                        checkpointGoalTick(kernel, boundGoalIds),
                        boundGoalIds,
                        checkpointHeldGoals,
                        totalTicks,
                        "dispatch-record-contention-escalation",
                        tickLines);
                    if (durable.Count > 0)
                        CompletePersistedOperatorIntents(durable, tickLines);
                }
            }
            else if (persistGoalTick is not null)
            {
                var criticalGoalIds = changedGoalIds
                    .Except(dispatchRecordWriteBoundGoalIds)
                    .ToArray();
                if (criticalGoalIds.Length > 0)
                {
                    PersistGoalTickOrThrow(persistGoalTick, kernel, criticalGoalIds, totalTicks, tickLines, busyWriteDelay);
                    CompletePersistedOperatorIntents(criticalGoalIds, tickLines);
                }

                if (dispatchRecordWriteBoundGoalIds.Count > 0)
                {
                    var boundGoalIds = dispatchRecordWriteBoundGoalIds.ToArray();
                    if (TryPersistGoalTick(
                            persistGoalTick,
                            kernel,
                            boundGoalIds,
                            totalTicks,
                            "dispatch-record-contention-escalation",
                            tickLines,
                            busyWriteDelay))
                    {
                        CompletePersistedOperatorIntents(boundGoalIds, tickLines);
                    }
                    else
                    {
                        EmitProgress(
                            $"DISPATCH_RECORD_ESCALATION_PERSIST_DEFERRED tick={totalTicks} goal={ResolveGoalContext(boundGoalIds, onlyGoalId: null)} reason=sqlite-busy-retry-exhausted",
                            tickLines);
                    }
                }
            }
            else
            {
                var tickPersisted = TryPersistTick(
                    persistTick,
                    kernel,
                    totalTicks,
                    ResolveGoalContext(changedGoalIds, onlyGoalId),
                    "tick",
                    tickLines,
                    busyWriteDelay);
                if (tickPersisted && changedGoalIds.Count > 0)
                {
                    CompletePersistedOperatorIntents(changedGoalIds, tickLines);
                }
            }
            SaveLandingTickBeforeRelaunch(
                pendingSelfRelaunch is not null, kernel, checkpointGoalTick, persistGoalTick, persistTick, checkpointHeldGoals, totalTicks, busyWriteDelay);

            var operatorDispositions = buildOperatorDispositions?.Invoke(kernel) ?? [];
            var tickSummary = new BatchTickSummary(totalTicks, tickAdvanced, tickHeld, tickEscalated, tickRetried, tickDone, WatchSleeping: false)
            {
                ProgressLines = tickLines,
                OperatorDispositions = operatorDispositions
            };
            if (drain.IsDraining && !watchSleep.IsStopRequested() && TryFinishDrain())
            {
                PublishTick(tickSummary, tickSnapshotBytes, activationSweepFollows: true);
                CompleteActivationTick(totalTicks);
                resolverTick?.Complete();
                break;
            }
            if (drain.IsDraining || (tickAdvanced == 0 && tickDone == 0))
            {
                if (watchInterval is null && !drain.IsDraining)
                {
                    if (ConductorWorkingSet.CountRecheckableNonTerminalGoals(
                            kernel,
                            onlyGoalId,
                            setAsideGoals,
                            transientRecheckableGoalIds: dispatchRecordWriteSkippedGoals.Concat(checkpointHeldGoals.Keys).ToHashSet(StringComparer.Ordinal)) > 0)
                    {
                        PublishTick(tickSummary, tickSnapshotBytes, activationSweepFollows: true);
                        CompleteActivationTick(totalTicks);
                        resolverTick?.Complete();
                        continue;
                    }

                    StopLoop("no-progress-no-watch");
                    Console.WriteLine($"[conduct --loop] No progress in tick {totalTicks}; all eligible goals held or escalated.");
                    DetachNonTerminalEligibleGoals(kernel, driver, onlyGoalId, excludedGoals, reapedGoals);
                    PersistGracefulDetachCheckpoint(
                        persistTick, persistGoalTick, kernel, totalTicks, onlyGoalId, "no-progress", tickLines, busyWriteDelay,
                        checkpointGoalTick, checkpointHeldGoals);
                    PublishTick(tickSummary, tickSnapshotBytes);
                    resolverTick?.Complete();
                    break;
                }

                var recheckableBlockedGoals = ConductorWorkingSet.CountRecheckableNonTerminalGoals(
                    kernel,
                    onlyGoalId,
                    setAsideGoals,
                        transientRecheckableGoalIds: dispatchRecordWriteSkippedGoals.Concat(checkpointHeldGoals.Keys).ToHashSet(StringComparer.Ordinal));
                var fallbackInterval = drain.BoundWait(ConsumeWatchInterval(
                    watchInterval ?? TimeSpan.FromSeconds(WatchStopPollIntervalSeconds), recheckableBlockedGoals));
                var sleepSeconds = (int)fallbackInterval.TotalSeconds;
                if (ConductorWorkingSet.CountRecheckableNonTerminalGoals(
                        kernel,
                        onlyGoalId,
                        setAsideGoals,
                        transientRecheckableGoalIds: dispatchRecordWriteSkippedGoals.Concat(checkpointHeldGoals.Keys).ToHashSet(StringComparer.Ordinal)) > 0)
                {
                    totalBlockedRechecks++;
                    UpdateBlockedRecheckRecurrences(sweepResult, setAsideGoals, blockedRecheckRecurrences);
                    var now = _utcNow();
                    if (lastBlockedRecheckHeartbeatAt is null ||
                        now - lastBlockedRecheckHeartbeatAt.Value >= _blockedRecheckHeartbeatInterval)
                    {
                        EmitProgress(FormatBlockedRecheckHeartbeat(blockedRecheckRecurrences, totalBlockedRechecks));
                        lastBlockedRecheckHeartbeatAt = now;
                    }
                }
                CompleteActivationTick(totalTicks);
                if (emitTickSummary)
                {
                    EmitProgress($"WATCH_SLEEP tick={totalTicks} seconds={sleepSeconds}");
                    Console.WriteLine($"[conduct --loop --watch] No progress in tick {totalTicks}; sleeping {sleepSeconds}s for workers to complete.");
                }
                PublishTick(tickSummary with { WatchSleeping = true }, tickSnapshotBytes);
                resolverTick?.Complete();

                var sleepResult = sleepFunc is not null
                    ? (sleepFunc(fallbackInterval) ? WatchSleepResult.StopRequested : WatchSleepResult.FallbackElapsed)
                    : watchSleep.Wait(fallbackInterval, wakeSignal, GetRunningDispatchExitCodePaths(kernel, onlyGoalId), GetRunningAttemptExitCodePaths(driver, kernel, onlyGoalId), ObserveDrain);

                if (sleepResult == WatchSleepResult.WakeSignaled)
                {
                    EmitProgress(FormatWatchWake(totalTicks, watchSleep.GetWakeStopFilePath(), wakeSignal));
                    pendingWakeMaintenanceKind = "wake-sweep";
                }

                if (sleepResult == WatchSleepResult.StopRequested || watchSleep.IsStopRequested())
                {
                    ReconcilePendingWakeMaintenance(totalTicks, tickLines);
                    stopRequested = true;
                    StopLoop("stop-file-during-sleep");
                    Console.WriteLine($"[conduct --loop --watch] Stop signal detected during sleep after tick {totalTicks}; no new dispatches.");
                    DetachNonTerminalEligibleGoals(kernel, driver, onlyGoalId, excludedGoals, reapedGoals);
                    PersistGracefulDetachCheckpoint(
                        persistTick, persistGoalTick, kernel, totalTicks, onlyGoalId, "stop-during-sleep", tickLines, busyWriteDelay,
                        checkpointGoalTick, checkpointHeldGoals);
                    break;
                }

                ObserveDrain();
                continue;
            }

            PublishTick(tickSummary, tickSnapshotBytes, activationSweepFollows: true);
            CompleteActivationTick(totalTicks);
            resolverTick?.Complete();
        }

        canaryDrain.Wait();
        ConductorLoopHandoffResult? handoff = selfRelaunchHandoff;
        if (selfRelaunchHandoff is not null && stopReason is null)
        {
            StopLoop("self-relaunch-handoff");
        }
        if (maxDurationReached && _handoffOnMaxDuration is not null)
        {
            var landedGoalDelta = GetCompletedGoalIds(kernel).Except(initiallyCompletedGoalIds, StringComparer.Ordinal).Count();
            var request = new ConductorLoopHandoffRequest(totalTicks, maxDuration ?? TimeSpan.Zero, totalDone, landedGoalDelta);
            handoff = _handoffOnMaxDuration(request);
            EmitHandoffProgress(totalTicks, handoff);
        }

        if (stopReason is null)
        {
            StopLoop("loop-return");
        }
        if (stopReason == ConductorDrainSignal.DrainedReason)
            EmitProgress(drain.FormatDrained(totalTicks));
        var observationRenewal = maxDurationReached || selfRelaunchHandoff is
            { Started: true, Reason: ConductorSelfRelaunch.SupervisorDelegatedReason };
        if (!observationRenewal)
            resolverObservation?.CloseOrderly(_utcNow);
        return new BatchLoopSummary(totalTicks, totalAdvanced, totalHeld, totalEscalated, totalRetried, totalDone, stopRequested, handoff, stopReason, totalBlockedRechecks, GetSuccessfulLandingCount(landedGoalIds))
        {
            HomeBuildTarget = handedOffHomeBuild,
            ResolverObservationHandoff = observationRenewal ? resolverObservation?.CloseForRenewal(_utcNow) : null
        };
        }
        catch (Exception ex)
        {
            stopReason ??= "unintended-exit";
            var detail = $"exception={Sanitize(ex.GetType().Name)} message={SanitizeReason(ex.Message)}";
            var diagnosticLocation = RecordUnintendedExitDiagnostic(totalTicks, ex);
            try
            {
                lifecycleSession?.Stop("unintended-exit", totalTicks, detail);
            }
            catch (Exception diagnosticException)
            {
                TryWriteAbnormalExitDiagnosticFailure("lifecycle", diagnosticException);
            }

            try
            {
                EmitProgress(
                    $"LOOP_STOP tick={totalTicks} rechecks={totalBlockedRechecks} reason=unintended-exit {detail} diagnostic={diagnosticLocation}");
            }
            catch (Exception diagnosticException)
            {
                TryWriteAbnormalExitDiagnosticFailure("event", diagnosticException);
            }

            throw;
        }
        finally
        {
            StopStewardAndAuthor();
            driver.AdmissionFacts.Stop();
            canaryDrain.DrainAndRetire();
            driver.SuccessfulLandingSink = previousSuccessfulLandingSink;
            driver.DispatchRecordWriteSucceededSink = previousDispatchRecordWriteSucceededSink;
            driver.LandingMutationBlocker = previousLandingMutationBlocker;
            foreach (var line in CurrentRetryDiagnostics.Value?.CompleteAll() ?? [])
                EmitProgress(line);
            CurrentRetryDiagnostics.Value = previousRetryDiagnostics;
            if (_mainFullVerification is { } finalVerification)
            {
                driver.ParallelAcceptanceAttemptCoordinator.GetMainFullVerificationRunId = null;
                RunJanitorialPhase("main-full-verification-stop", totalTicks, () => { finalVerification.Dispose(); return true; });
            }
            _mainFullVerification = null;
            CurrentConductEventLogWriter.Value = previousConductEventLogWriter;
        }
    }
    private static void TryWriteAbnormalExitDiagnosticFailure(string sink, Exception exception)
    {
        try
        {
            Console.Error.WriteLine(
                $"[conduct --loop] Failed to record unintended exit in {sink}: " +
                $"{exception.GetType().Name}: {SanitizeReason(exception.Message)}");
        }
        catch
        {
        }
    }

    private bool TryAdvanceGoal(
        Func<ConductorAdvanceResult?> advance,
        AgentOrchestratorKernel kernel,
        ConductorDriver driver,
        Goal goal,
        ConductorAutonomyPolicy policy,
        int totalTicks,
        string label,
        List<string> changedGoalLines,
        Dictionary<string, string> lastGoalDisposition,
        HashSet<GoalId> changedGoalIds,
        HashSet<string> escalatedGoals,
        HashSet<string> reapedGoals,
        Dictionary<string, BatchSetAsideEntry> setAsideGoals,
        Dictionary<string, BatchSetAsideEntry> selfClearedSetAsideEntries,
        Dictionary<string, int> dispatchRecordWriteSkips,
        HashSet<string> dispatchRecordWriteSkippedGoals,
        HashSet<GoalId> dispatchRecordWriteBoundGoalIds,
        List<string> tickLines,
        ref int tickHeld,
        ref int tickEscalated,
        Action<string> finishGoalWalk,
        out ConductorAdvanceResult result)
    {
        try
        {
            var advanceResult = advance();
            _slotContentionHolds.Clear(goal.Id);
            if (advanceResult is null)
            {
                result = null!;
                return false;
            }

            result = advanceResult;
            return true;
        }
        catch (DispatchRecordWriteException ex)
        {
            if (ex.IsFatal)
                throw;

            dispatchRecordWriteSkips.TryGetValue(goal.Id.Value, out var priorSkips);
            var skips = priorSkips + 1;
            dispatchRecordWriteSkips[goal.Id.Value] = skips;
            var code = ex.SqliteErrorCode?.ToString() ?? "unavailable";
            var (token, disposition) = ex.Cause == DispatchRecordWriteFailureCause.Contention
                ? ("DISPATCH_RECORD_WRITE_CONTENTION", "contention")
                : ("DISPATCH_RECORD_WRITE_UNCLASSIFIED", "unclassified");
            var diagnostic = ex.Cause is null
                ? $" exception={ex.InnerException?.GetType().FullName ?? "unavailable"} error={SanitizeReason(ex.InnerException?.Message ?? ex.Message)}"
                : string.Empty;
            EmitProgress(
                $"{token} tick={totalTicks} kind={ex.Kind} goal={label} task={ShortGoalId(ex.TaskId.Value)} sqliteCode={code} skip={skips}/{DispatchRecordContentionSkipLimit}{diagnostic}",
                tickLines);

            if (skips < DispatchRecordContentionSkipLimit)
            {
                dispatchRecordWriteSkippedGoals.Add(goal.Id.Value);
                tickHeld++;
                finishGoalWalk($"dispatch-record-{disposition}");
                result = null!;
                return false;
            }

            if (TrySkipGoalLeftWorkingSet(kernel, goal, totalTicks, "advance-fault", tickLines, finishGoalWalk, out result)) return false;

            var reason = $"dispatch-record-write-{disposition}-limit count={skips}/{DispatchRecordContentionSkipLimit} sqliteCode={code}";
            changedGoalLines.Add($"GOAL goal={label} result=escalated reason={reason}");
            lastGoalDisposition[goal.Id.Value] = changedGoalLines[^1];
            changedGoalIds.Add(goal.Id);
            dispatchRecordWriteBoundGoalIds.Add(goal.Id);
            kernel.ClearGoalHold(goal.Id);
            kernel.RecordGoalPolicyDecision(goal.Id, $"Batch loop tick {totalTicks}: {reason}");
            escalatedGoals.Add(goal.Id.Value);
            ReapGoalOnce(kernel, kernel.GetGoal(goal.Id), reapedGoals, totalTicks, tickLines);
            SetAside(kernel, driver, kernel.GetGoal(goal.Id), BatchSetAsideCondition.AdvanceFault, setAsideGoals, selfClearedSetAsideEntries);
            tickEscalated++;
            finishGoalWalk($"dispatch-record-{disposition}-limit");
            result = null!;
            return false;
        }
        catch (Exception ex) when (ex is DotnetBuildSlotsBusyException or BuildLockBlockedException)
        {
            if (TrySkipGoalLeftWorkingSet(kernel, goal, totalTicks, "build-slot-contention", tickLines, finishGoalWalk, out result))
            {
                _slotContentionHolds.Clear(goal.Id);
                return false;
            }

            var holds = _slotContentionHolds.RecordHold(goal.Id);
            if (holds == SlotContentionAttentionHoldLimit)
            {
                var detail = ex is DotnetBuildSlotsBusyException busy
                    ? FormatSlotsBusy(busy.SlotsBusy)
                    : FormatBuildLockBlocked(((BuildLockBlockedException)ex).Attribution);
                EmitProgress($"INFRASTRUCTURE_ATTENTION tick={totalTicks} kind=build-slot-contention goal={label} holds={holds} {detail}", tickLines);
            }

            tickHeld++;
            finishGoalWalk("build-slot-contention");
            result = null!;
            return false;
        }
        catch (Exception ex)
        {
            if (TrySkipGoalLeftWorkingSet(kernel, goal, totalTicks, "advance-fault", tickLines, finishGoalWalk, out result)) return false;

            var msg = $"Batch loop tick {totalTicks}: fault isolating goal — advance threw: {SanitizeReason(ex.Message)}";
            changedGoalLines.Add($"GOAL goal={label} result=escalated reason={SanitizeReason(ex.Message)}");
            lastGoalDisposition[goal.Id.Value] = changedGoalLines[^1];
            changedGoalIds.Add(goal.Id);
            kernel.ClearGoalHold(goal.Id);
            Console.WriteLine($"[conduct --loop] Tick {totalTicks}: {label} [{policy.Name}] → escalated (advance threw): {ex.Message}");
            kernel.RecordGoalPolicyDecision(goal.Id, msg);
            escalatedGoals.Add(goal.Id.Value);
            ReapGoalOnce(kernel, goal, reapedGoals, totalTicks, tickLines);
            SetAsideAdvanceFault(kernel, driver, goal, SanitizeReason(ex.Message), setAsideGoals, selfClearedSetAsideEntries);
            tickEscalated++;
            finishGoalWalk("advance-fault");
            result = null!;
            return false;
        }
    }

    private static void EmitHandoffProgress(
        int tick,
        ConductorLoopHandoffResult handoff,
        string? goalId = null)
    {
        var goal = goalId is null ? string.Empty : $" goal={goalId}";
        if (handoff.Started)
        {
            EmitProgress($"LOOP_HANDOFF tick={tick}{goal} pid={handoff.ProcessId} stdout={SanitizeHandoffDetail(handoff.StdoutPath ?? "")} stderr={SanitizeHandoffDetail(handoff.StderrPath ?? "")} verification={SanitizeHandoffDetail(handoff.VerificationOutcome ?? "unknown")}");
            Console.WriteLine($"[conduct --loop] Handoff started successor pid={handoff.ProcessId} log={handoff.StdoutPath}");
            return;
        }

        if (handoff.Failed)
        {
            EmitProgress($"LOOP_HANDOFF_FAILED tick={tick} reason={SanitizeHandoffDetail(handoff.Reason ?? "unknown")} stdout={SanitizeHandoffDetail(handoff.StdoutPath ?? "")} stderr={SanitizeHandoffDetail(handoff.StderrPath ?? "")} verification={SanitizeHandoffDetail(handoff.VerificationOutcome ?? "unknown")}");
            Console.WriteLine($"[conduct --loop] Handoff failed: {handoff.Reason ?? "unknown"}");
            return;
        }

        EmitProgress($"LOOP_HANDOFF_SKIPPED tick={tick} reason={SanitizeReason(handoff.Reason ?? "not-started")}");
        Console.WriteLine($"[conduct --loop] Handoff skipped: {handoff.Reason ?? "not-started"}");
    }

    private static void EmitSelfRelaunchRollback(
        int tick,
        string goalId,
        string phase,
        string reason) =>
        EmitProgress(
            $"LOOP_RELAUNCH_ROLLBACK tick={tick} goal={goalId} phase={SanitizeHandoffDetail(phase)} " +
            $"rolledBack=true continuing=true reason={SanitizeHandoffDetail(reason)}");

    private T? RunJanitorialPhase<T>(
        string phase,
        int tick,
        Func<T> action)
    {
        var startTimestamp = _janitorialTimestamp();
        try
        {
            _janitorialPhaseProbe?.Invoke(phase);
            var result = action();

            if (_consecutiveJanitorialFailures.Remove(phase, out var skippedTicks))
            {
                EmitProgress(
                    $"LOOP_JANITORIAL_RECOVERED tick={tick} phase={SanitizeHandoffDetail(phase)} skippedTicks={skippedTicks}");
            }

            return result;
        }
        catch (SqliteException ex) when (SqliteOrchestratorStateRepository.IsTransientLock(ex))
        {
            RecordFailure(ex, transient: true);
            return default;
        }
        catch (Exception ex)
        {
            RecordFailure(ex, transient: false);
            return default;
        }
        finally
        {
            var elapsedMilliseconds = (long)Stopwatch.GetElapsedTime(startTimestamp, _janitorialTimestamp()).TotalMilliseconds;
            _tickPhaseElapsedMs[phase] = _tickPhaseElapsedMs.GetValueOrDefault(phase) + elapsedMilliseconds;
        }

        void RecordFailure(Exception exception, bool transient)
        {
            var consecutiveFailures = _consecutiveJanitorialFailures.GetValueOrDefault(phase) + 1;
            _consecutiveJanitorialFailures[phase] = consecutiveFailures;
            EmitProgress(
                $"LOOP_JANITORIAL_FAILED tick={tick} phase={SanitizeHandoffDetail(phase)} transient={transient.ToString().ToLowerInvariant()} " +
                $"attempts=1 consecutiveFailures={consecutiveFailures} exception={exception.GetType().Name} " +
                $"message={SanitizeHandoffDetail(exception.Message)}");
            if (consecutiveFailures == JanitorialFailureEscalationThreshold)
            {
                EmitProgress(
                    $"LOOP_JANITORIAL_DEGRADED tick={tick} phase={SanitizeHandoffDetail(phase)} " +
                    $"consecutiveFailures={consecutiveFailures} workDeferred=true");
            }
        }
    }

    private static string ResolveGoalContext(AgentOrchestratorKernel kernel, string? onlyGoalId)
    {
        if (onlyGoalId is not null)
        {
            return ShortGoalId(onlyGoalId);
        }

        var active = kernel.Goals
            .Where(goal => !ConductorWorkingSet.IsTerminalGoal(goal))
            .Select(goal => ShortGoalId(goal.Id.Value))
            .Take(4)
            .ToArray();
        return active.Length == 0 ? "none" : string.Join(",", active);
    }

    private static string ResolveGoalContext(IReadOnlyCollection<GoalId> goalIds, string? onlyGoalId)
    {
        if (goalIds.Count > 0)
        {
            return string.Join(",", goalIds.Select(goalId => ShortGoalId(goalId.Value)).Take(4));
        }

        return onlyGoalId is null ? "none" : ShortGoalId(onlyGoalId);
    }

    private IReadOnlySet<string> ProtectedOperatorIntentGoalIds(
        AgentOrchestratorKernel kernel, Dictionary<string, GoalSnapshotCheckpointResult> checkpointHeldGoals) =>
        checkpointHeldGoals.Keys.Concat(kernel.Goals
                .Where(goal => _operatorIntents?.HasPendingCompletion(goal.Id.Value) == true)
                .Select(goal => goal.Id.Value))
            .ToHashSet(StringComparer.Ordinal);

    private ConductorOperatorIntentBoundary.Result ServiceOperatorIntentBoundary(
        AgentOrchestratorKernel kernel, ConductorDriver driver, string? onlyGoalId,
        Dictionary<string, GoalSnapshotCheckpointResult> checkpointHeldGoals,
        int tick, Action<AgentOrchestratorKernel>? persistTick,
        Action<AgentOrchestratorKernel, IReadOnlyCollection<GoalId>>? persistGoalTick,
        Func<AgentOrchestratorKernel, IReadOnlyCollection<GoalId>, IReadOnlyList<GoalSnapshotCheckpointResult>>? checkpointGoalTick,
        Action<TimeSpan>? busyWriteDelay,
        IReadOnlyList<OperatorIntentRecord>? capturedBatch = null)
    {
        var result = ConductorOperatorIntentBoundary.Apply(
            kernel, driver, onlyGoalId, checkpointHeldGoals, _operatorIntents,
            capturedBatch is null ? _prepareOperatorIntentTarget : null, _goalReloadObservation, capturedBatch);
        if (result.ChangedGoalIds.Count == 0) return result;
        var requested = result.ChangedGoalIds.ToArray();
        if (checkpointGoalTick is not null)
            result.DurableGoalIds.UnionWith(ApplyCheckpointOutcomes(
                checkpointGoalTick(kernel, requested), requested, checkpointHeldGoals, tick,
                "operator-intent", result.Lines));
        else if (persistGoalTick is not null)
        {
            PersistGoalTickOrThrow(persistGoalTick, kernel, requested, tick, result.Lines, busyWriteDelay);
            result.DurableGoalIds.UnionWith(requested);
        }
        else if (TryPersistTick(persistTick, kernel, tick, ResolveGoalContext(requested, onlyGoalId),
                     "operator-intent", result.Lines, busyWriteDelay))
            result.DurableGoalIds.UnionWith(requested);

        if (result.DurableGoalIds.Count > 0)
            CompletePersistedOperatorIntents(result.DurableGoalIds, result.Lines);
        return result;
    }

    internal static string ShortGoalId(string goalId) =>
        goalId.Length <= 8 ? goalId : goalId[..8];

    private static bool RecordChangedDisposition(
        string goalId,
        string progressLine,
        Dictionary<string, string> lastGoalDisposition,
        List<string> changedGoalLines,
        bool alwaysRecord = false,
        bool emitUnchanged = false)
    {
        var changed = alwaysRecord
            || !lastGoalDisposition.TryGetValue(goalId, out var previous)
            || !string.Equals(previous, progressLine, StringComparison.Ordinal);
        if (changed) lastGoalDisposition[goalId] = progressLine;
        // Repeated cohort approval holds stay visible without repeating the journal decision.
        if (changed || emitUnchanged) changedGoalLines.Add(progressLine);
        return changed;
    }

    private static bool ShouldAlwaysEmitDisposition(ConductorAdvanceOutcome outcome) =>
        outcome is ConductorAdvanceOutcome.Held { State: GoalLifecycleState.AwaitingVerification };

    private static bool TryReconcileAwaitingVerificationHold(
        AgentOrchestratorKernel kernel,
        Goal goal,
        ConductorAdvanceResult result,
        int tick,
        out ConductorAdvanceOutcome reconciledOutcome)
    {
        reconciledOutcome = result.Outcome;
        if (result.Outcome is not ConductorAdvanceOutcome.Held { State: GoalLifecycleState.AwaitingVerification })
        {
            return false;
        }

        var reason = $"Batch loop tick {tick}: reconciled all task verification gates; promoted goal to Verified.";
        if (!kernel.ReconcileGoalVerificationStatus(goal.Id, reason))
        {
            return false;
        }

        reconciledOutcome = new ConductorAdvanceOutcome.Executed(
            GoalLifecycleState.AwaitingVerification,
            "Reconciled all task verification gates; goal advanced to Verified");
        return true;
    }

    private static string Sanitize(string value) => ConductorDiagnosticText.Sanitize(value);

    private static string SanitizeReceiptToken(string value) =>
        value.Replace(' ', '_').Replace('\t', '_').Replace('\n', '_').Replace('\r', '_');

    internal static string SanitizeReason(string value) => ConductorDiagnosticText.SanitizeReason(value);

    private static string SanitizeHeartbeatDetail(string value) =>
        ConductorHeartbeatDetail.Sanitize(value);

    private static string SanitizeHandoffDetail(string value) =>
        value.Replace(' ', '_').Replace('\t', '_').Replace('\n', '_').Replace('\r', '_');

    private static bool PoliciesMatch(ConductorAutonomyPolicy left, ConductorAutonomyPolicy right) =>
        string.Equals(left.ToJson(), right.ToJson(), StringComparison.Ordinal);

    private IReadOnlyDictionary<string, ParallelLandingOutcome> ReconcileDrainingAcceptance(
        IReadOnlyList<Goal> goals, AgentOrchestratorKernel kernel, ConductorDriver driver,
        ConductorAutonomyPolicy policy, int tick, List<string> changedGoalLines, HashSet<GoalId> changedGoalIds)
    {
        var state = new ParallelAcceptanceBatchState
        {
            Results = new Dictionary<string, ParallelLandingOutcome>(StringComparer.Ordinal)
        };
        ReconcileParallelAcceptanceAttempts(state, goals, kernel, driver, policy, tick, changedGoalLines,
            changedGoalIds, reconcileOnly: true);
        RunJanitorialPhase("drain-grouped-reconcile", tick, () =>
        {
            driver.SweepCompletedCohortGateRuns();
            var remaining = ConductorAcceptanceCohortTerminalReceiptConsumption.PrepareAdmission(
                driver, goals, changedGoalIds, tick, changedGoalLines);
            return MergeTrainAttributedFailureReconciliation.PrepareAdmission(
                driver, remaining, policy, changedGoalIds, changedGoalLines);
        });
        return state.Results;
    }

    private IReadOnlyDictionary<string, ParallelLandingOutcome> RunParallelAcceptanceBatch(
        IReadOnlyList<Goal> eligible,
        IReadOnlyList<Goal> scopedGoals,
        IReadOnlySet<GoalId> verifiedGoalIdsAtTickStart,
        IReadOnlySet<GoalId> preWalkIntentChangedGoalIds,
        AgentOrchestratorKernel kernel,
        ConductorDriver driver,
        ConductorAutonomyPolicy policy,
        HashSet<string> completedGoals,
        HashSet<string> escalatedGoals,
        int tick,
        List<string> changedGoalLines,
        HashSet<GoalId> changedGoalIds,
        Func<bool> suppressNewAcceptanceAdmission)
    {
        var configuredAcceptanceWidth = policy.AcceptanceWidth;
        if (configuredAcceptanceWidth < ConductorAutonomyPolicy.MinimumAcceptanceWidth)
        {
            return new Dictionary<string, ParallelLandingOutcome>(StringComparer.Ordinal);
        }
        var state = ConductorTickStepLedger.Measure("attempt-census", () => BeginParallelAcceptanceBatch(
            eligible, kernel, driver, policy, completedGoals, escalatedGoals,
            configuredAcceptanceWidth, tick, changedGoalLines));
        ConductorTickStepLedger.Measure("activation-probe-record", () => RecordActivationAdmissionWaitingForProbe(state));
        if (state.CapacityStateUnavailable) return state.Results;

        ConductorTickStepLedger.Measure("attempt-reconcile", () => ReconcileParallelAcceptanceAttempts(state, scopedGoals, kernel, driver, policy, tick, changedGoalLines, changedGoalIds));
        ConductorTickStepLedger.Measure("grouped-admission-prep", () => PrepareGroupedAcceptanceAdmission(
            state, eligible, scopedGoals, verifiedGoalIdsAtTickStart, preWalkIntentChangedGoalIds,
            kernel, driver, policy, completedGoals, escalatedGoals, tick, changedGoalLines, changedGoalIds,
            suppressNewAcceptanceAdmission));
        ConductorTickStepLedger.Measure("candidate-supersession", () => _candidateSupersessionTick.Apply(
            kernel, driver, CandidateSupersessionStop, _operatorIntents?.GateCancellation,
            line => RecordParallelAcceptanceProgress(line, changedGoalLines), changedGoalIds));
        var (priorityOccupantsBefore, priorityResultsBefore, priorityBaseOrder) = ConductorTickStepLedger.Measure("priority-order", () =>
            (GoalPriorityStarvationGuard.GateOccupants(state.LiveAttempts.Select(attempt => attempt.GoalId), driver),
             state.Results.Keys.ToHashSet(StringComparer.Ordinal), GoalSchedulingOrder.BaseAcceptance(state.OrderedEligible)));
        RunJanitorialPhase("selected-execution-backstop-admission", tick, () =>
        {
            _selectedExecutionBackstop?.ServiceAsync().GetAwaiter().GetResult();
            return true;
        });
        if (!suppressNewAcceptanceAdmission())
            ConductorTickStepLedger.Measure("merge-train", () => { if (PairTrainAdmission.CanAdmitTrain(state.GroupedAdmissionOpen, suppressNewAcceptanceAdmission(), DecideLiveAcceptanceAdmission(state.AcceptanceCensus, configuredAcceptanceWidth).IsAdmitted)) AdmitMergeTrain(state, driver, kernel, policy, tick, changedGoalLines); });
        ConductorTickStepLedger.Measure("pair-cohort", () => AdmitPairCohort(state, kernel, driver, policy, tick, changedGoalLines, changedGoalIds,
            suppressNewAcceptanceAdmission));
        ConductorTickStepLedger.Measure("hold-cohort-members", () => HoldCohortMembers(state, driver, policy, tick, changedGoalLines));
        if (suppressNewAcceptanceAdmission()) return state.Results;
        ConductorTickStepLedger.Measure("hold-lone-ready", () => HoldLoneReadyGoalForInReviewCohortPartner(kernel, scopedGoals, state.OrderedEligible, state.ProductionCandidates,
            state.AcceptanceCensus, driver, policy, state.Results, tick, changedGoalLines));
        if (!suppressNewAcceptanceAdmission())
            ConductorTickStepLedger.Measure("solo-admission", () => AdmitSoloAcceptance(state, kernel, driver, policy, tick, changedGoalLines, changedGoalIds,
                suppressNewAcceptanceAdmission));
        ConductorTickStepLedger.Measure("priority-guard", () => _priorityStarvationGuard.ObserveGateTick(state.OrderedEligible, priorityBaseOrder, priorityOccupantsBefore, GoalPriorityStarvationGuard.GateOccupants(state.LiveAttempts.Select(attempt => attempt.GoalId), driver).Concat(state.Results.Where(pair => pair.Value.SlotIndex is not null && !priorityResultsBefore.Contains(pair.Key)).Select(pair => pair.Key)).ToHashSet(StringComparer.Ordinal), tick, changedGoalIds, changedGoalLines.Add));
        ConductorTickStepLedger.Measure("deferral-summary", () => SummarizeAcceptanceDeferrals(state, tick, changedGoalLines));
        if (_mainFullVerification is { } mainVerification && !suppressNewAcceptanceAdmission())
            RunJanitorialPhase("main-full-verification-admission", tick, () => mainVerification.Service(
                Math.Max(state.DeferredByAdmission, state.Results.Values.Count(entry =>
                    entry.Result.Outcome is ConductorAdvanceOutcome.Held { Owner: ConductorHoldOwner.AcceptanceQueue })),
                () => DecideLiveAcceptanceAdmission(state.AcceptanceCensus, configuredAcceptanceWidth).IsAdmitted));

        return state.Results;
    }

    private static string BoundSingleLine(string value) => ConductorDiagnosticText.BoundSingleLine(value);

    private static ConductorAdvanceResult EscalateParallelAcceptanceSafely(
        ConductorDriver driver,
        Goal goal,
        ConductorAutonomyPolicy policy,
        string reason)
    {
        try
        {
            return driver.EscalateParallelLandingAcceptance(goal, policy, reason);
        }
        catch (Exception escalationException)
        {
            var fallbackReason = BoundSingleLine(
                $"{reason}; escalation recording failed: {escalationException.GetType().Name}: {escalationException.Message}");
            return new ConductorAdvanceResult(
                goal.Id.Value,
                goal.Id.Value[..8],
                policy.Name,
                new ConductorAdvanceOutcome.Escalated(GoalLifecycleState.Verified, fallbackReason));
        }
    }

    private static string FormatSlotsBusy(DotnetBuildLeaseAcquisition.SlotsBusy slotsBusy)
    {
        var slots = string.Join(
            ",",
            slotsBusy.BusySlots.Select(slot =>
                $"slot-{slot.SlotIndex}:pid-{slot.OwnerProcessId?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown"}"));
        return $"wanted-by={slotsBusy.WantedBy}; busy={slots}";
    }

    private static string FormatBuildLockBlocked(BuildLockAttribution attribution)
    {
        var holders = attribution.Holders.Count == 0
            ? "unknown"
            : string.Join(", ", attribution.Holders.Select(holder =>
                $"pid {holder.ProcessId?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown"} {holder.ProcessName ?? "unknown"}"));
        return $"path={attribution.Path}; holders: {holders}";
    }

    private void ReapNonTerminalEligibleGoals(
        AgentOrchestratorKernel kernel,
        string? onlyGoalId,
        HashSet<string> excludedGoals,
        HashSet<string> reapedGoals, int tick, List<string>? tickLines)
    {
        foreach (var goal in kernel.Goals)
        {
            if (onlyGoalId is not null && goal.Id.Value != onlyGoalId)
            {
                continue;
            }

            if (excludedGoals.Contains(goal.Id.Value) || ConductorWorkingSet.IsTerminalGoal(goal))
            {
                continue;
            }

            ReapGoalOnce(kernel, goal, reapedGoals, tick, tickLines);
        }
    }

    private void DetachNonTerminalEligibleGoals(
        AgentOrchestratorKernel kernel,
        ConductorDriver driver,
        string? onlyGoalId,
        HashSet<string> excludedGoals,
        HashSet<string> reapedGoals)
    {
        driver.PrepareAcceptanceHolderDetach();
        foreach (var goal in kernel.Goals)
        {
            if (onlyGoalId is not null && goal.Id.Value != onlyGoalId)
            {
                continue;
            }

            if (excludedGoals.Contains(goal.Id.Value) || ConductorWorkingSet.IsTerminalGoal(goal))
            {
                continue;
            }

            DetachGoalOnce(kernel, goal, reapedGoals);
        }
    }

    private void DetachGoalOnce(AgentOrchestratorKernel kernel, Goal goal, HashSet<string> reapedGoals)
    {
        if (!reapedGoals.Add(goal.Id.Value))
        {
            return;
        }

        _detachGoalRunningDispatches(kernel, goal);
    }

    internal static WatchSleepResult SleepUntilNextTick(
        TimeSpan interval,
        string stopFilePath,
        IConductorWakeSignal? wakeSignal,
        IReadOnlyList<string> trackedExitCodePaths,
        IReadOnlyList<string>? runningAttemptExitCodePaths = null) =>
        new ConductorWatchSleep(stopFilePath).Wait(interval, wakeSignal, trackedExitCodePaths, runningAttemptExitCodePaths);

}

internal enum WatchSleepResult
{
    FallbackElapsed,
    StopRequested,
    WakeSignaled,
    DrainRequested
}

internal sealed record ParallelLandingOutcome(ConductorAdvanceResult Result, int? SlotIndex);

internal sealed record GoalWalkTiming(string Goal, string Result, TimeSpan Elapsed);

public sealed record ConductorLoopHandoffRequest(
    int Tick,
    TimeSpan MaxDuration,
    int Done,
    int LandedGoalDelta = 0);
