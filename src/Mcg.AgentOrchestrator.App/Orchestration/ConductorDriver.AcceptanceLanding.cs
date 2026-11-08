using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorDriver
{
    private readonly Action<string, string> _acceptanceEventSink;
    internal MultiRepositoryLandingHoldAttention? MultiRepositoryLandingHoldAttention { get; set; }
    internal MultiRepositoryAcceptanceGate? RepositoryAcceptanceGate { get; set; }
    internal MultiRepositoryTupleLanding? RepositoryTupleLanding { get; set; }
    private readonly Func<AcceptanceEngineHealthSnapshot?> _readAcceptanceEngineHealth;
    private readonly Action<TimeSpan> _noTickAcceptancePollDelay;
    private readonly TimeSpan _noTickAcceptancePollTimeout;
    internal static readonly TimeSpan DefaultNoTickAcceptancePollTimeout = TimeSpan.FromHours(2);
    private static readonly TimeSpan NoTickAcceptancePollInterval = TimeSpan.FromMilliseconds(100);

    internal ConductorAdvanceResult? TryReplayPassedAcceptance(Goal suppliedGoal, ConductorAutonomyPolicy policy)
    {
        var goal = GetCurrentGoal(suppliedGoal);
        if (_executionDirectory is not null)
            PassedReceiptManifestInvalidation.Check(goal, _parallelAcceptanceAttemptCoordinator,
                _executionDirectory, () => _resolveAcceptanceHeads(goal).BranchHeadSha);
        if (!_parallelAcceptanceAttemptCoordinator.HasPassedReconciledReceipt(goal.Id.Value))
            return null;
        var candidate = TryBuildParallelAcceptanceCandidate(goal, policy, 0, out var declineReason);
        if (candidate is null && declineReason != CandidateDeclineHoldActive)
            return null;
        // An active landing hold does not invalidate a passed gate. Validate its proof
        // under receipt custody before returning that hold, rather than queueing a gate.
        if (candidate is null)
        {
            var heldHeads = _resolveAcceptanceHeads(goal);
            candidate = ConductorParallelAcceptanceCandidate.Create(goal, 0, _getLandingFileScopes(goal),
                heldHeads.BranchHeadSha, heldHeads.MainHeadSha);
        }
        using var mutation = _tryAcquireEvidenceMutationLease(goal, "conductor:acceptance-receipt-replay");
        if (mutation is null)
            return ReplacementEvidenceMutationHeld(goal, candidate.GoalPrefix, policy);

        goal = GetCurrentGoal(goal);
        var facts = GetFacts(goal);
        if (goal.Status == GoalStatus.Completed || facts.IsMerged)
            return MakeResult(goal.Id.Value, candidate.GoalPrefix, policy,
                new ConductorAdvanceOutcome.Done(GoalLifecycle.ResolveState(goal, facts)));
        if (GoalLifecycle.ResolveState(goal, facts) is not (GoalLifecycleState.Verified or GoalLifecycleState.Verifying) ||
            !AcceptancePrecheck.HasCompletedPassedVerificationForAllTasks(goal) ||
            ((_cohortKernel ?? _conductorTickKernel)?.BuildVerificationGate(goal.Id).IsSatisfied == false) ||
            HasPendingDeferredNoChangeEvidence(goal) || HasRoutableRecordedCohortAttributionFailure(goal))
            return null;
        var circuitHold = PassedAcceptanceReceiptReplay.CircuitHold(goal, policy, _readAcceptanceEngineHealth());
        if (circuitHold is not null)
            return circuitHold;
        var heads = _resolveAcceptanceHeads(goal);
        candidate = ConductorParallelAcceptanceCandidate.Create(goal, 0, _getLandingFileScopes(goal),
            heads.BranchHeadSha, heads.MainHeadSha);
        var operations = new PassedAcceptanceReceiptReplay.LandingOperations(
            original => RebaseBeforeMerge(goal, original.GoalPrefix, policy, out _),
            () => _resolveAcceptanceHeads(goal),
            CarryForwardGreenVerdict,
            (original, acceptance) => CompleteLandingAfterAcceptance(goal, original.GoalPrefix, policy, acceptance),
            (attempt, mainSha) =>
            {
                if (_executionDirectory is not null)
                    GoalOperationJournal.Completed(_executionDirectory, goal, "conductor:acceptance-receipt-replay",
                        $"attempt={attempt.AttemptId} gatesAvoided=1 " +
                        $"greenToLandingMs={(_utcNow() - (attempt.CompletedAt ?? attempt.StartedAt)).TotalMilliseconds:F0}", mainSha);
            });
        return _parallelAcceptanceAttemptCoordinator.ReplayPassedReconciledReceipt(candidate,
            decision =>
            {
                if (ReplayLandingHold(goal, candidate.GoalPrefix, policy) is { } hold)
                {
                    var original = decision.Run!.Candidate;
                    var current = _resolveAcceptanceHeads(goal);
                    if ((original.BranchHeadSha != current.BranchHeadSha || original.MainHeadSha != current.MainHeadSha) &&
                        CarryForwardGreenVerdict(original, decision.Run.Acceptance!) is not
                            (AcceptanceMainAdvanceDisposition.Unchanged or AcceptanceMainAdvanceDisposition.Disjoint))
                        return null;
                    return hold;
                }
                return PassedAcceptanceReceiptReplay.Apply(decision, operations);
            },
            decision =>
            {
                var current = GetCurrentGoal(goal);
                var currentHeads = _resolveAcceptanceHeads(current);
                if (GoalLifecycle.ResolveState(current, GetFacts(current)) is not (GoalLifecycleState.Verified or GoalLifecycleState.Verifying) ||
                    HasActiveApparatusHold(current, out _) || HasActiveOwnerReviewHold(current, out _, out _) ||
                    PassedAcceptanceReceiptReplay.CircuitHold(current, policy, _readAcceptanceEngineHealth()) is not null)
                    return false;
                return AcceptanceCriterionEvidence.RecordExactReplayEvidence(current, decision.Attempt,
                    currentHeads, _cohortKernel ?? _conductorTickKernel);
            });
    }

    private ConductorAdvanceResult? ReplayLandingHold(Goal goal, string prefix, ConductorAutonomyPolicy policy)
    {
        if (HasActiveOwnerReviewHold(goal, out var sha, out var receipt))
            return OwnerReviewHeld(goal, prefix, policy, sha, receipt!);
        if (!HasActiveApparatusHold(goal, out _))
            return null;
        var failure = goal.LatestAcceptanceFailure!;
        var decision = VerifiedAdmissionPolicy.Evaluate(new VerifiedAdmissionFacts(goal.Status)
        {
            ApparatusHold = true, ApparatusBranchSha = failure.BranchHeadSha ?? "unknown",
            ApparatusMainSha = failure.MainHeadSha ?? "unknown",
            ApparatusCandidate = FormatAcceptanceCandidate(failure.BranchHeadSha, failure.MainHeadSha)
        });
        return MakeResult(goal.Id.Value, prefix, policy,
            new ConductorAdvanceOutcome.Held(GoalLifecycleState.Verified, decision.Reason,
                StableIdentity: decision.StableIdentity.Length == 0 ? null : decision.StableIdentity)
            { Decision = decision.ToRecord() });
    }

    private ConductorAdvanceResult ExecuteLanding(Goal goal, string goalPrefix, ConductorAutonomyPolicy policy)
    {
        if (MultiRepositoryGoal.Applies(goal)) return ExecuteRepositoryAcceptance(goal, goalPrefix, policy, GoalLifecycleState.Verified);
        var facts = new VerifiedAdmissionFacts(goal.Status);
        if (HasActiveOwnerReviewHold(goal, out var ownerSha, out var ownerReceipt))
            return OwnerReviewHeld(goal, goalPrefix, policy, ownerSha, ownerReceipt!,
                VerifiedAdmissionPolicy.Evaluate(facts with
                {
                    OwnerReviewHold = true, OwnerReviewSha = ownerSha, OwnerReviewReason = ownerReceipt!.Reason,
                    OwnerReviewFingerprint = ownerReceipt.Fingerprint ?? "none"
                }));
        facts = facts with { OwnerReviewHold = false };

        if (HasRoutableRecordedCohortAttributionFailure(goal))
        {
            return RouteRecordedCohortAttributionFailure(goal, goalPrefix, policy,
                VerifiedAdmissionPolicy.Evaluate(facts with
                {
                    CohortAttribution = true,
                    CohortAttributionEvidence = goal.LatestAcceptanceFailure!.CheckAttributions![0].Evidence
                }));
        }
        facts = facts with { CohortAttribution = false };
        if (HasActiveApparatusHold(goal, out _))
        {
            var failure = goal.LatestAcceptanceFailure!;
            return AdmissionHeld(facts with
            {
                ApparatusHold = true, ApparatusBranchSha = failure.BranchHeadSha ?? "unknown",
                ApparatusMainSha = failure.MainHeadSha ?? "unknown",
                ApparatusCandidate = FormatAcceptanceCandidate(failure.BranchHeadSha, failure.MainHeadSha)
            });
        }
        facts = facts with { ApparatusHold = false };

        var sharedAcceptance = TryRunFallbackAcceptance(goal, goalPrefix, policy, GoalLifecycleState.Verified);
        if (sharedAcceptance is not null)
        {
            return sharedAcceptance;
        }

        using var evidenceMutationLease = _tryAcquireEvidenceMutationLease(goal, "conductor:acceptance-and-land");
        if (evidenceMutationLease is null)
        {
            var result = ReplacementEvidenceMutationHeld(goal, goalPrefix, policy);
            var held = (ConductorAdvanceOutcome.Held)result.Outcome;
            var decision = VerifiedAdmissionPolicy.Evaluate(facts with
                { EvidenceLease = false, EvidenceLeaseReason = held.Reason });
            return result with { Outcome = held with { Decision = decision.ToRecord() } };
        }
        facts = facts with { EvidenceLease = true };

        if (!AcceptancePrecheck.HasCompletedPassedVerificationForAllTasks(goal))
        {
            return AdmissionHeld(facts with { AllTasksPassed = false });
        }
        facts = facts with { AllTasksPassed = true };

        var early = RebaseBeforeAcceptance(goal, goalPrefix, policy, applySideEffects: true, out _);
        if (early is not null)
        {
            return early;
        }

        // Gate 2: acceptance verification (test suite quality check) on the integrated worktree.
        if (EnvironmentGate?.Check(goal, policy, GoalLifecycleState.Verified, forGate: true) is { } environmentHold) return environmentHold;
        var gateStart = VerifiedGateStartExecutor.Execute(
            () => RunInlineLandingSourceSizePreflight(goal),
            () => _runAcceptanceVerification(
                    goal,
                    null,
                    null,
                    CancellationToken.None,
                    SoloLandingGateExecutionOptions.Create(goal)));
        if (gateStart.DeferralKind is null)
        {
            return CompleteLandingAfterAcceptance(goal, goalPrefix, policy, gateStart.Summary!);
        }

        return AdmissionHeld(facts with
        {
            GateStartDeferral = gateStart.DeferralKind,
            InfrastructureDeferredReasonCode = gateStart.InfrastructureDeferredReasonCode,
            InfrastructureDeferredMessage = gateStart.InfrastructureDeferredMessage,
            BuildSlotsBusyDetail = gateStart.SlotsBusy is null ? null : FormatSlotsBusy(gateStart.SlotsBusy),
            BuildLockBlockedDetail = gateStart.BuildLockAttribution is null ? null : FormatBuildLockBlocked(gateStart.BuildLockAttribution),
            CancellationProbeCause = gateStart.CancellationCause
        });

        ConductorAdvanceResult AdmissionHeld(VerifiedAdmissionFacts observed)
        {
            var decision = VerifiedAdmissionPolicy.Evaluate(observed);
            return MakeResult(goal.Id.Value, goalPrefix, policy,
                new ConductorAdvanceOutcome.Held(GoalLifecycleState.Verified, decision.Reason,
                    StableIdentity: decision.StableIdentity.Length == 0 ? null : decision.StableIdentity)
                { Decision = decision.ToRecord() });
        }
    }

    private ConductorAdvanceResult ExecuteRepositoryAcceptance(Goal goal, string prefix,
        ConductorAutonomyPolicy policy, GoalLifecycleState state)
    {
        if (HasActiveOwnerReviewHold(goal, out var ownerSha, out var ownerReceipt))
            return OwnerReviewHeld(goal, prefix, policy, ownerSha, ownerReceipt!);
        using var lease = _tryAcquireEvidenceMutationLease(goal, "conductor:multi-repository-acceptance");
        if (lease is null) return ReplacementEvidenceMutationHeld(goal, prefix, policy);
        if (EnvironmentGate?.Check(goal, policy, state, forGate: true) is { } environmentHold) return environmentHold;
        var result = MultiRepositoryAcceptanceExecution.Run(goal, state, RepositoryAcceptanceGate, RepositoryTupleLanding,
            () => RunInlineLandingSourceSizePreflight(goal) ?? _runAcceptanceVerification(goal, null, null, CancellationToken.None, SoloLandingGateExecutionOptions.Create(goal)),
            () => _land(goal, policy), () => LandingMutationBlocker?.Invoke() is { Length: > 0 } blocked
                ? blocked : InFlightTrainLandingHold.Describe(goal.Id, GetActiveAcceptanceCohortCapacity()), () => _clearAcceptanceFailure(goal),
            home => { SuccessfulLandingSink?.Invoke(new ConductorLandingReceipt(goal.Id.Value, home.ChangedFiles ?? [], home.MergeCommitSha)); _afterSuccessfulLanding(goal, home); });
        if (result.Hold is { } held) return MakeResult(goal.Id.Value, prefix, policy, held);
        if (result.Failure is { } failure)
        {
            _recordAcceptanceFailure(goal, failure.FailedChecks!, failure.BranchHeadSha, failure.MainHeadSha, null, null);
            return CompleteLandingAfterAcceptance(goal, prefix, policy, failure);
        }
        return MakeResult(goal.Id.Value, prefix, policy, new ConductorAdvanceOutcome.Executed(GoalLifecycleState.Verified, "Landed: multi-repository tuple"));
    }

    private ConductorAdvanceResult ExecuteVerifying(
        Goal goal,
        string goalPrefix,
        ConductorAutonomyPolicy policy)
    {
        if (MultiRepositoryGoal.Applies(goal)) return ExecuteRepositoryAcceptance(goal, goalPrefix, policy, GoalLifecycleState.Verifying);
        if (!_isConductorTick)
        {
            var decision = VerifyingStagePolicy.Evaluate(new(
                GoalLifecycleState.Verifying, _isConductorTick, goal.Status));
            return MakeResult(
                goal.Id.Value,
                goalPrefix,
                policy,
                new ConductorAdvanceOutcome.Held(
                    GoalLifecycleState.Verifying,
                    decision.Reason) { Decision = decision.ToRecord() });
        }

        var fallback = TryRunFallbackAcceptance(goal, goalPrefix, policy, GoalLifecycleState.Verifying, out var facts);
        if (fallback is not null)
            return fallback;

        var background = VerifyingStagePolicy.Evaluate(facts);
        return MakeResult(
            goal.Id.Value,
            goalPrefix,
            policy,
            new ConductorAdvanceOutcome.Held(
                GoalLifecycleState.Verifying,
                background.Reason) { Decision = background.ToRecord() });
    }

    private ConductorAdvanceResult? TryRunFallbackAcceptance(
        Goal goal,
        string goalPrefix,
        ConductorAutonomyPolicy policy,
        GoalLifecycleState callerState)
        => TryRunFallbackAcceptance(goal, goalPrefix, policy, callerState, out _);

    private ConductorAdvanceResult? TryRunFallbackAcceptance(
        Goal goal,
        string goalPrefix,
        ConductorAutonomyPolicy policy,
        GoalLifecycleState callerState,
        out VerifyingStageFacts facts)
    {
        facts = new(callerState, _isConductorTick, goal.Status,
            ParallelAcceptanceEnabled: _parallelAcceptanceEnabled);

        ConductorAdvanceResult Hold(GoalLifecycleState state, VerifyingStageFacts observed,
            ConductorHoldOwner owner = ConductorHoldOwner.None)
        {
            var hold = VerifyingStagePolicy.Evaluate(observed);
            return MakeResult(goal.Id.Value, goalPrefix, policy,
                new ConductorAdvanceOutcome.Held(state, hold.Reason) { Owner = owner, Decision = hold.ToRecord() });
        }

        if (!_parallelAcceptanceEnabled)
        {
            return null;
        }

        if (goal.Status == GoalStatus.Completed)
        {
            return Hold(GoalLifecycleState.Verified, facts);
        }

        ConductorParallelAcceptanceCandidate? candidate;
        try
        {
            candidate = TryBuildParallelAcceptanceCandidate(goal, policy, slotIndex: 0, out _);
        }
        catch (EvidenceMutationLeaseUnavailableException)
        {
            var result = ReplacementEvidenceMutationHeld(goal, goalPrefix, policy);
            var held = (ConductorAdvanceOutcome.Held)result.Outcome;
            facts = facts with { ReplacementLeaseAvailable = false, ReplacementLeaseReason = held.Reason };
            var hold = VerifyingStagePolicy.Evaluate(facts);
            return result with { Outcome = held with { Decision = hold.ToRecord() } };
        }

        facts = facts with { ReplacementLeaseAvailable = candidate is null ? null : true, CandidateBuilt = candidate is not null };
        if (candidate is null)
        {
            return null;
        }

        var replay = VerifyingAttemptExecutor.ExecuteReplay(
            () => TryReplayPassedAcceptance(goal, policy), out var replayWriterBusyMessage);
        if (replayWriterBusyMessage is not null)
        {
            facts = facts with { ArtifactWriterBusy = true, ArtifactWriterMessage = replayWriterBusyMessage };
            return Hold(GoalLifecycleState.Verified, facts);
        }
        if (replay is not null)
            return replay;

        if (_isConductorTick &&
            policy.AcceptanceWidth >= ConductorAutonomyPolicy.MinimumAcceptanceWidth &&
            _parallelAcceptanceAttemptCoordinator.GetUnreconciledAttempts([goal.Id.Value]).Count == 0)
        {
            // Capture at launch time so attempts started earlier in this tick consume width.
            var admission = ConductorBatchLoop.DecideFallbackAcceptanceAdmission(
                _parallelAcceptanceAttemptCoordinator,
                _conductorTickKernel?.Goals.Where(active => active.Status != GoalStatus.Completed).ToArray() ?? [goal],
                GetActiveAcceptanceCohortCapacity(),
                goal.Id.Value,
                Math.Min(GetAcceptanceSlotCount(goal), policy.AcceptanceWidth),
                _conductorTick);
            facts = facts with { AdmissionAdmitted = admission.IsAdmitted, AdmissionReason = admission.Reason };
            if (!admission.IsAdmitted)
            {
                return Hold(callerState, facts);
            }
        }

        if (EnvironmentGate?.Check(goal, policy, callerState, forGate: true) is { } environmentHold) return environmentHold;
        ConductorParallelAcceptanceRunAcceptance runAcceptance = _isConductorTick
            ? RunParallelLandingAcceptance
            : (attemptCandidate, attemptPolicy, lease, cancellationToken, executionOptions) =>
                RunParallelLandingAcceptance(
                    attemptCandidate,
                    attemptPolicy,
                    lease,
                    cancellationToken,
                    executionOptions,
                    omitStableSlotIndexWithoutLease: true);
        var execution = VerifyingAttemptExecutor.Execute(
            _isConductorTick,
            exhaustion => _parallelAcceptanceAttemptCoordinator.Evaluate(candidate, policy, runAcceptance, exhaustion),
            attempt => _parallelAcceptanceAttemptCoordinator.ObserveExistingAttempt(attempt, candidate),
            attempt => EmitNoTickAcceptanceLifecycle(candidate, "started", attempt),
            _utcNow,
            _noTickAcceptancePollDelay,
            NoTickAcceptancePollInterval,
            _noTickAcceptancePollTimeout);
        if (execution.ArtifactWriterBusyMessage is { } busyMessage)
        {
            facts = facts with { ArtifactWriterBusy = true, ArtifactWriterMessage = busyMessage };
            return Hold(GoalLifecycleState.Verified, facts);
        }

        var decision = execution.Decision!;
        facts = facts with
        {
            ArtifactWriterBusy = false,
            AttemptDecisionKind = decision.Kind.ToString(),
            AttemptId = decision.Attempt.AttemptId
        };
        if (execution.NoTickWaitOutcome is { } waitOutcome)
        {
            facts = facts with { NoTickWaitOutcome = waitOutcome };
            return Hold(GoalLifecycleState.Verified, facts,
                waitOutcome == VerifyingAttemptExecutor.DeadlineElapsed
                    ? ConductorHoldOwner.BackgroundAttempt
                    : ConductorHoldOwner.None);
        }

        switch (decision.Kind)
        {
            case ConductorParallelAcceptanceAttemptDecisionKind.Started:
            case ConductorParallelAcceptanceAttemptDecisionKind.Running:
                if (_conductorTickKernel is not null)
                {
                    ConductorBatchLoop.MarkParallelAcceptanceStarted(
                        _conductorTickKernel,
                        goal,
                        decision.Attempt,
                        _conductorTick);
                }

                facts = facts with { GoalStatus = goal.Status };
                return Hold(GoalLifecycleState.Verifying, facts);

            case ConductorParallelAcceptanceAttemptDecisionKind.Completed:
                var run = decision.Run ?? ConductorParallelAcceptanceRunResult.Fault(
                    candidate,
                    new InvalidOperationException("Completed acceptance attempt had no run result."));
                if (_conductorTickKernel is not null)
                {
                    ConductorBatchLoop.ReconcileParallelAcceptanceTerminalState(
                        _conductorTickKernel,
                        goal,
                        run,
                        decision.Attempt);
                }

                var result = ConductorBatchLoop.CompleteParallelAcceptanceRun(
                    this,
                    policy,
                    run,
                    decision.Attempt,
                    out var evidenceMutationLeaseHeld);
                ConductorBatchLoop.MarkParallelAcceptanceReconciledUnlessLeaseHeld(
                    this,
                    run,
                    evidenceMutationLeaseHeld,
                    decision.Attempt);
                if (!_isConductorTick)
                {
                    EmitNoTickAcceptanceLifecycle(
                        candidate,
                        ConductorBatchLoop.AcceptanceRunDisposition(run),
                        decision.Attempt);
                }
                return result;

            case ConductorParallelAcceptanceAttemptDecisionKind.TerminalWithoutRun:
                if (_conductorTickKernel is not null)
                {
                    ConductorBatchLoop.ReconcileParallelAcceptanceTerminalState(
                        _conductorTickKernel,
                        goal,
                        decision.Attempt);
                }

                var terminal = ConductorBatchLoop.ParallelAcceptanceTerminal(
                    this,
                    candidate,
                    policy,
                    decision.Attempt);
                _parallelAcceptanceAttemptCoordinator.MarkReconciled(decision.Attempt);
                if (!_isConductorTick)
                {
                    EmitNoTickAcceptanceLifecycle(
                        candidate,
                        ConductorBatchLoop.AcceptanceAttemptOutcomeToken(decision.Attempt.Outcome),
                        decision.Attempt);
                }
                return terminal;

            default:
                throw new InvalidOperationException(
                    $"Unsupported acceptance attempt decision '{decision.Kind}'.");
        }
    }

    private void EmitNoTickAcceptanceLifecycle(
        ConductorParallelAcceptanceCandidate candidate,
        string result,
        ConductorParallelAcceptanceAttempt attempt)
    {
        try
        {
            _acceptanceEventSink(
                candidate.GoalPrefix,
                AcceptanceLifecycleEventFormatter.Format(
                    candidate.GoalPrefix,
                    candidate.SlotIndex,
                    result,
                    attempt.AttemptId,
                    tick: 0));
        }
        catch
        {
            // Advisory observability cannot replace a computed acceptance verdict.
        }
    }

    private static (Action<string, string> EventSink, Action<TimeSpan> PollDelay, TimeSpan PollTimeout)
        CreateProductionAcceptanceWaitConfiguration(OrchestratorWorkspace workspace)
    {
        var writer = new ConductEventLogWriter(workspace.ConductEventsLogPath);
        return ((goalId, detail) => writer.Append("acceptance", goalId, detail), Thread.Sleep,
            DefaultNoTickAcceptancePollTimeout);
    }

    internal ConductorDriver(
        AgentOrchestratorKernel kernel,
        OrchestratorWorkspace workspace,
        IGoalAcceptanceVerifier acceptanceVerifier,
        IReadOnlyList<AgentDefinition> agents,
        WorkerProfileCatalog profiles,
        ConductorParallelAcceptanceAttemptCoordinator parallelAcceptanceAttemptCoordinator, GoalWorktreeCleanupHooks? cleanupHooks = null)
        : this(kernel, workspace, acceptanceVerifier, agents, profiles, cleanupHooks: cleanupHooks)
    {
        _parallelAcceptanceAttemptCoordinator = parallelAcceptanceAttemptCoordinator;
    }

    internal ConductorParallelAcceptanceRunResult RunParallelLandingAcceptance(
        ConductorParallelAcceptanceCandidate candidate,
        ConductorAutonomyPolicy policy,
        DotnetBuildEnvironmentLease? stableSlotLease,
        CancellationToken cancellationToken,
        AcceptanceRunExecutionOptions executionOptions) =>
        RunParallelLandingAcceptance(
            candidate,
            policy,
            stableSlotLease,
            cancellationToken,
            executionOptions,
            omitStableSlotIndexWithoutLease: false);

    internal ConductorParallelAcceptanceRunResult RunParallelLandingAcceptance(
        ConductorParallelAcceptanceCandidate candidate,
        ConductorAutonomyPolicy policy,
        DotnetBuildEnvironmentLease? stableSlotLease,
        CancellationToken cancellationToken,
        AcceptanceRunExecutionOptions executionOptions,
        bool omitStableSlotIndexWithoutLease)
    {
        if (EnvironmentGate?.Check(candidate.Goal, policy, GoalLifecycleState.Verifying, forGate: true) is { } environmentHold)
            return ConductorParallelAcceptanceRunResult.Early(candidate, environmentHold, null);
        executionOptions = executionOptions with { IntegrationBranch = _integrationBranch, ProjectHomeDirectory = _cohortWorkspace?.ProjectHomeDirectoryOrNull };
        return ParallelLandingAcceptanceExecutor.Execute(candidate,
            () => _tryAcquireEvidenceMutationLease(candidate.Goal, "conductor:parallel-acceptance"),
            () => ReplacementEvidenceMutationHeld(candidate.Goal, candidate.GoalPrefix, policy),
            () =>
            {
                var early = RebaseBeforeAcceptance(candidate.Goal, candidate.GoalPrefix, policy,
                    applySideEffects: false, out var earlyOutcome);
                return early is null ? null : ConductorParallelAcceptanceRunResult.Early(candidate, early, earlyOutcome);
            },
            () => RefreshParallelAcceptanceCandidate(candidate),
            effective => _runAcceptanceVerification(effective.Goal,
                omitStableSlotIndexWithoutLease && stableSlotLease is null ? null : effective.SlotIndex,
                stableSlotLease, cancellationToken, executionOptions));
    }
}
