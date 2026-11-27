using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorBatchLoop
{
    private readonly PreLandingMergeConflictEscalationLedger _preLandingMergeConflictLedger = new();

    private ParallelAcceptanceBatchState BeginParallelAcceptanceBatch(
        IReadOnlyList<Goal> eligible,
        AgentOrchestratorKernel kernel,
        ConductorDriver driver,
        ConductorAutonomyPolicy policy,
        HashSet<string> completedGoals,
        HashSet<string> escalatedGoals,
        int configuredAcceptanceWidth,
        int tick,
        List<string> changedGoalLines)
    {
        var state = new ParallelAcceptanceBatchState
        {
            ConfiguredAcceptanceWidth = configuredAcceptanceWidth
        };
        state.Results = new Dictionary<string, ParallelLandingOutcome>(StringComparer.Ordinal);
        state.DeferredByAdmission = 0;
        state.OrderedEligible = OrderParallelAcceptanceEligibleGoals(eligible
            .Where(goal => IsParallelAcceptanceInputEligible(goal, driver, kernel, completedGoals, escalatedGoals))
            .ToArray());
        var activeReservations = BuildActiveParallelAcceptanceReservations(
            driver.ParallelAcceptanceAttemptCoordinator,
            kernel.Goals.ToArray());
        if (activeReservations.Failure is { } capacityFailure)
        {
            var reason =
                $"acceptance capacity state unavailable; retry on next conduct tick: {SanitizeReason(capacityFailure.Message)}";
            foreach (var goal in state.OrderedEligible)
            {
                state.Results[goal.Id.Value] = new ParallelLandingOutcome(
                    TrainEjectionReport.DescribeWaitingResult(driver, goal.Id, ParallelAcceptanceHeld(goal, policy, reason)),
                    null);
            }

            RecordParallelAcceptanceProgress(
                $"ADMISSION tick={tick} result=held reason=acceptance-capacity-state-unavailable detail={SanitizeReason(capacityFailure.Message)}",
                changedGoalLines);
            state.CapacityStateUnavailable = true;
            return state;
        }

        state.LiveAttempts = activeReservations.Attempts.ToList();
        state.ActiveCandidates = activeReservations.Candidates;
        state.ActiveAttemptIds = activeReservations.AttemptIds;
        state.ActiveAttemptSlotIndexes = activeReservations.StableSlotIndexes;
        return state;
    }

    private static bool IsParallelAcceptanceInputEligible(Goal goal, ConductorDriver driver,
        AgentOrchestratorKernel kernel, HashSet<string> completedGoals, HashSet<string> escalatedGoals) =>
        ParallelAcceptanceLifecycleEligibility.IsParallelAcceptanceLifecycleEligible(goal, driver) &&
        !driver.HasRoutableRecordedCohortAttributionFailure(goal) &&
        goal.Status is GoalStatus.Verified or GoalStatus.Verifying &&
        AcceptancePrecheck.HasCompletedPassedVerificationForAllTasks(goal) &&
        SliceBatchParentExecutionGuard.TryDescribeStreamCompleteHold(goal) is null &&
        !ConductorDriver.HasPendingDeferredNoChangeEvidence(goal) &&
        GetDependencyHoldReason(goal, completedGoals, escalatedGoals, kernel) is null &&
        VerifiedAcceptanceEscalationDecision.TryHasUnresolvedPersistedVerifiedAcceptanceEscalation(goal, driver) == false;

    private void ReconcileParallelAcceptanceAttempts(
        ParallelAcceptanceBatchState state,
        IReadOnlyList<Goal> scopedGoals,
        AgentOrchestratorKernel kernel,
        ConductorDriver driver,
        ConductorAutonomyPolicy policy,
        int tick,
        List<string> changedGoalLines,
        HashSet<GoalId> changedGoalIds,
        bool reconcileOnly = false)
    {
        // Admission candidates exclude parks; already-owned attempts still need their terminal
        // evidence consumed. Do not put these goals into OrderedEligible or start new work.
        var goalsToReconcile = reconcileOnly ? scopedGoals : state.OrderedEligible.Concat(scopedGoals.Where(goal =>
                     goal.IsExplicitlyParked &&
                     driver.ParallelAcceptanceAttemptCoordinator.GetUnreconciledAttempts([goal.Id.Value]).Count > 0))
                     .DistinctBy(goal => goal.Id);
        foreach (var goal in goalsToReconcile)
        {
            try
            {
            var sameGoalAttempts = driver.ParallelAcceptanceAttemptCoordinator.GetUnreconciledAttempts([goal.Id.Value]);
            if (reconcileOnly && sameGoalAttempts.Count == 0)
                continue;
            if (PassedAcceptanceReceiptReplay.TryBeforeAdmission(sameGoalAttempts.Count, goal, driver, policy,
                    () => _acceptanceEngineCircuit?.Read(), state.Results, changedGoalIds, tick, changedGoalLines,
                    RecordParallelAcceptanceProgress))
                continue;

            var (running, retainedTerminal) = ParallelAcceptanceAttemptReconciliation.ObserveAndReconcileSiblings(
                driver.ParallelAcceptanceAttemptCoordinator, goal, sameGoalAttempts);

            if (running.Length > 0)
            {
                state.Results[goal.Id.Value] = ReserveRunningParallelAcceptanceAttempts(
                    kernel,
                    driver,
                    goal,
                    policy,
                    tick,
                    running,
                    changedGoalLines,
                    changedGoalIds);
                continue;
            }

            var verificationGate = kernel.BuildVerificationGate(goal.Id);
            if (!verificationGate.IsSatisfied)
            {
                var blockingReasons = string.Join(
                    ',',
                    verificationGate.Tasks
                        .Where(task => task.GateStatus != VerificationGateStatus.Passed)
                        .Select(task => $"{task.Role}:{task.Reason}"));
                var reason = BoundSingleLine(
                    $"inconsistent {goal.Status} state: authoritative task verification gate unsatisfied ({blockingReasons}); " +
                    "apply verify-manual or retry before acceptance");
                state.Results[goal.Id.Value] = new ParallelLandingOutcome(
                    EscalateParallelAcceptanceSafely(driver, goal, policy, reason),
                    null);
                RecordParallelAcceptanceProgress(
                    $"ADMISSION tick={tick} result=escalated reason=authoritative-verification-gate-unsatisfied goal={goal.Id.Value[..8]} detail={SanitizeReason(reason)}",
                    changedGoalLines);
                continue;
            }

            var engineHealth = _acceptanceEngineCircuit?.Read();
            if (IsAcceptanceEngineCircuitHoldRequired(goal.Status, engineHealth))
            {
                state.Results[goal.Id.Value] = new ParallelLandingOutcome(
                    ParallelAcceptanceHeld(
                        goal,
                        policy,
                        BuildAcceptanceEngineHoldReason(engineHealth!)),
                    null);
                RecordParallelAcceptanceProgress(
                    $"ADMISSION tick={tick} result=held reason=acceptance-engine-circuit goal={goal.Id.Value[..8]} health={engineHealth.Health}",
                    changedGoalLines);
                continue;
            }

            if (retainedTerminal is null)
            {
                continue;
            }

            ConductorAcceptanceLeaseReceipts.Replay(driver, retainedTerminal.Attempt, changedGoalLines);
            if (retainedTerminal.Kind == ConductorParallelAcceptanceAttemptDecisionKind.Completed)
            {
                var run = retainedTerminal.Run ?? ConductorParallelAcceptanceRunResult.Fault(
                    ConductorParallelAcceptanceCandidate.Create(
                        goal,
                        retainedTerminal.Attempt.SlotIndex,
                        retainedTerminal.Attempt.ScopePaths ?? [],
                        retainedTerminal.Attempt.BranchHeadSha,
                        retainedTerminal.Attempt.MainHeadSha),
                    new InvalidOperationException("Completed acceptance attempt had no run result."));
                ReconcileParallelAcceptanceTerminalState(kernel, goal, run, retainedTerminal.Attempt);
                if (reconcileOnly)
                {
                    // The finished gate owns its result. Landing or re-gating would require
                    // a new acceptance lease, so retain the settled receipt for normal replay.
                    driver.ParallelAcceptanceAttemptCoordinator.MarkReconciled(retainedTerminal.Attempt);
                }
                else
                {
                    var result = CompleteParallelAcceptanceRun(
                        driver,
                        policy,
                        run,
                        retainedTerminal.Attempt,
                        out var evidenceMutationLeaseHeld);
                    MarkParallelAcceptanceReconciledUnlessLeaseHeld(
                        driver,
                        run,
                        evidenceMutationLeaseHeld,
                        retainedTerminal.Attempt);
                    state.Results[goal.Id.Value] = new ParallelLandingOutcome(result, retainedTerminal.Attempt.SlotIndex);
                }
                changedGoalIds.Add(goal.Id);
                RecordParallelAcceptanceProgress(
                    AcceptanceLifecycleEventFormatter.Format(goal.Id.Value[..8], retainedTerminal.Attempt.SlotIndex, AcceptanceRunDisposition(run), retainedTerminal.Attempt.AttemptId, tick),
                    changedGoalLines);
                continue;
            }

            ReconcileParallelAcceptanceTerminalState(kernel, goal, retainedTerminal.Attempt);
            state.Results[goal.Id.Value] = new ParallelLandingOutcome(
                ParallelAcceptanceTerminal(
                    driver,
                    ConductorParallelAcceptanceCandidate.Create(
                        goal,
                        retainedTerminal.Attempt.SlotIndex,
                        retainedTerminal.Attempt.ScopePaths ?? [],
                        retainedTerminal.Attempt.BranchHeadSha,
                        retainedTerminal.Attempt.MainHeadSha),
                    policy,
                    retainedTerminal.Attempt),
                retainedTerminal.Attempt.SlotIndex);
            driver.ParallelAcceptanceAttemptCoordinator.MarkReconciled(retainedTerminal.Attempt);
            changedGoalIds.Add(goal.Id);
            RecordParallelAcceptanceProgress(
                AcceptanceLifecycleEventFormatter.Format(goal.Id.Value[..8], retainedTerminal.Attempt.SlotIndex, AcceptanceAttemptOutcomeToken(retainedTerminal.Attempt.Outcome), retainedTerminal.Attempt.AttemptId, tick),
                changedGoalLines);
            }
            catch (AcceptanceArtifactWriterLeaseBusyException ex)
            {
                state.Results[goal.Id.Value] = new ParallelLandingOutcome(
                    ParallelAcceptanceHeld(
                        goal,
                        policy,
                        $"acceptance artifact writer busy; retry on next conduct tick. {ex.Message}"),
                    null);
                RecordParallelAcceptanceProgress(
                    $"ADMISSION tick={tick} result=deferred reason=acceptance-artifact-writer-busy goal={goal.Id.Value[..8]} detail={SanitizeReason(ex.Message)}",
                    changedGoalLines);
            }
            catch (Exception ex)
            {
                var reason = BoundSingleLine(
                    $"persisted parallel acceptance reconciliation fault isolated: {ex.GetType().Name}: {ex.Message}");
                state.Results[goal.Id.Value] = new ParallelLandingOutcome(
                    EscalateParallelAcceptanceSafely(driver, goal, policy, reason),
                    null);
                RecordParallelAcceptanceProgress(
                    $"ADMISSION tick={tick} result=escalated reason=persisted-acceptance-reconciliation-fault goal={goal.Id.Value[..8]} detail={SanitizeReason(reason)}",
                    changedGoalLines);
            }
        }
    }

    private void PrepareGroupedAcceptanceAdmission(
        ParallelAcceptanceBatchState state,
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
        state.OrderedEligible = ConductorTickStepLedger.Measure("prep-cohort-receipts", () => ConductorAcceptanceCohortTerminalReceiptConsumption.PrepareAdmission(driver, state.OrderedEligible, changedGoalIds, tick, changedGoalLines, state.Results));
        state.OrderedEligible = ConductorTickStepLedger.Measure("prep-train-reconcile", () => MergeTrainAttributedFailureReconciliation.PrepareAdmission(driver, state.OrderedEligible, policy, changedGoalIds, changedGoalLines));
        var speculativeCandidates = ConductorTickStepLedger.Measure("prep-projection", () => GroupedAdmissionCandidateProjection.ProjectAll(
            driver.ProjectionCache.Retain(eligible, state.OrderedEligible), driver, policy, kernel, tick, line => RecordParallelAcceptanceProgress(line, changedGoalLines), scopedGoals.Where(goal => preWalkIntentChangedGoalIds.Contains(goal.Id))));
        state.OrderedEligible = AdmissionFactsPendingAdmission.Apply(state.OrderedEligible, speculativeCandidates, state.Results, policy, tick, line => RecordParallelAcceptanceProgress(line, changedGoalLines), driver);
        state.ActiveCohortCapacity = driver.GetActiveAcceptanceCohortCapacity();
        state.AcceptanceCensus = ConductorTickStepLedger.Measure("prep-census", () => CaptureLiveAcceptanceCensus(
            state.LiveAttempts, state.ActiveAttemptIds, state.ActiveCohortCapacity, tick,
            changedGoalLines, blockAdmissionOnFailure: true, _mainFullVerification?.ActiveRunId));
        ConductorTickStepLedger.Measure("prep-plan-receipt", () => EmitSpeculativeCohortPlanReceipt(scopedGoals, verifiedGoalIdsAtTickStart,
            preWalkIntentChangedGoalIds, eligible, state.OrderedEligible, speculativeCandidates,
            state.LiveAttempts, state.ActiveCohortCapacity, state.AcceptanceCensus, driver, policy,
            completedGoals, escalatedGoals, kernel, tick));
        state.LiveAttemptGoalIds = state.LiveAttempts.Select(attempt => attempt.GoalId).ToHashSet(StringComparer.Ordinal);
        var activeCohortMemberGoalIds = ConductorTickStepLedger.Measure("prep-cohort-members", () => driver.GetActiveCohortGateMemberGoalIds((memberGoalIds, detail) =>
        {
            var memberIds = memberGoalIds.OrderBy(id => id, StringComparer.Ordinal).ToArray();
            EmitProgress(
                $"ACCEPTANCE_COHORT_INFLIGHT tick={tick} goal={memberIds[0][..8]} " +
                $"members={string.Join(',', memberIds.Select(id => id[..8]))} {detail}");
        }));
        state.CohortEligible = state.OrderedEligible
            .Where(goal => !state.LiveAttemptGoalIds.Contains(goal.Id.Value) &&
                           !activeCohortMemberGoalIds.Contains(goal.Id.Value))
            .ToArray();
        state.ProductionCandidates = ConductorTickStepLedger.Measure("prep-obligation-filter", () => GroupedAdmissionCandidateFilter.Filter(ExcludeGroupedAcceptanceCandidatesWithNonAcceptanceObligations(
            speculativeCandidates, state.CohortEligible, state.LiveAttemptGoalIds, activeCohortMemberGoalIds), kernel.Goals, activeCohortMemberGoalIds));
        ConductorTickStepLedger.Measure("prep-merge-conflict", () => { foreach (var goal in state.CohortEligible.ToArray())
        {
            if (state.Results.ContainsKey(goal.Id.Value) ||
                speculativeCandidates.First(candidate => candidate.GoalId == goal.Id).ProjectionResult is not
                    GateReadyCandidateProjectionResult.Excluded { Reason: GateReadyCandidateExclusionReason.MergeConflict } exclusion)
                continue;
            var result = driver.TryEscalatePreLandingMergeConflict(goal, policy, exclusion,
                fingerprint => _preLandingMergeConflictLedger.IsEscalated(goal.Id.Value, fingerprint), out var revisionFingerprint);
            if (result is null) continue;
            state.Results[goal.Id.Value] = new ParallelLandingOutcome(result, null);
            _preLandingMergeConflictLedger.Record(goal.Id.Value, revisionFingerprint!);
            state.CohortEligible = state.CohortEligible.Where(candidate => candidate.Id != goal.Id).ToArray();
            RecordParallelAcceptanceProgress(
                $"ADMISSION tick={tick} result=escalated reason=pre-landing-merge-conflict goal={goal.Id.Value[..8]}", changedGoalLines);
        }});
        var trainAdmission = DecideLiveAcceptanceAdmission(state.AcceptanceCensus, state.ConfiguredAcceptanceWidth);
        (state.CohortEligible, state.ProductionCandidates) = ConductorTickStepLedger.Measure("prep-approval-hold", () => PassedCohortReceiptApprovalHoldAdmission.ApplyBeforeGating(
            driver, policy, kernel, state.CohortEligible, state.ProductionCandidates, scopedGoals, preWalkIntentChangedGoalIds,
            goal => IsParallelAcceptanceInputEligible(goal, driver, kernel, completedGoals, escalatedGoals),
            state.LiveAttemptGoalIds.Concat(activeCohortMemberGoalIds).ToHashSet(StringComparer.Ordinal),
            state.Results, tick, line => RecordParallelAcceptanceProgress(line, changedGoalLines), suppressNewAcceptanceAdmission,
            goal => IsAcceptanceEngineCircuitHoldRequired(goal.Status, _acceptanceEngineCircuit?.Read()), changedGoalIds, _utcNow()));
        (state.CohortEligible, state.ProductionCandidates) = ConductorTickStepLedger.Measure("prep-train-receipts", () => LandPassedMergeTrainReceiptsBeforeAdmission(
            driver, policy, state.CohortEligible, state.ProductionCandidates, scopedGoals, state.Results, tick, changedGoalLines, kernel));
        state.OrderedEligible = PassedAcceptanceReceiptReplay.ExcludeRefusedCarryHolds(state.OrderedEligible, state.Results);
        state.OldestWaiterObservation = suppressNewAcceptanceAdmission()
            ? new ParallelAcceptanceOldestWaiterObservation(null, 0)
            : ConductorTickStepLedger.Measure("prep-oldest-waiter", () => ObserveOldestParallelAcceptanceWaiter(state.OrderedEligible, state.LiveAttemptGoalIds));
        state.TransientRetryPriorityGoal = !suppressNewAcceptanceAdmission() &&
            (driver.MergeTrainsEnabled || driver.AcceptanceCohortsEnabled)
            ? ConductorTickStepLedger.Measure("prep-retry-priority", () => SoloAcceptanceTransientRetryPriority.Select(
                state.CohortEligible, state.Results, goal => EvaluateSoloAcceptanceAdmissibility(kernel, driver, goal, policy,
                    state.AcceptanceCensus, state.ConfiguredAcceptanceWidth, state.ActiveAttemptSlotIndexes, state.ActiveCandidates,
                    state.LiveAttemptGoalIds, state.OldestWaiterObservation, oldestServedThisTick: false, tick),
                driver.ParallelAcceptanceAttemptCoordinator.CountPendingTransientRetries, tick, changedGoalLines))
            : null;
        var interactionOnlyKeys = ConductorTickStepLedger.Measure("prep-interaction-keys", () => driver.ReadInteractionOnlyMemberKeys());
        var interactionOnlyGoalIds = speculativeCandidates
            .Where(candidate => candidate.ProjectionResult is GateReadyCandidateProjectionResult.Ready ready &&
                interactionOnlyKeys.Contains(ConductorAcceptanceCohortAttributedMembers.Key(
                    candidate.GoalId, ready.Projection.CandidateRevision)))
            .Select(candidate => candidate.GoalId.Value).ToHashSet(StringComparer.Ordinal);
        var workspaceProbeBackoffGoalIds = ConductorTickStepLedger.Measure("prep-probe-backoff", () => speculativeCandidates
            .Where(candidate => candidate.ProjectionResult is GateReadyCandidateProjectionResult.Ready ready &&
            driver.ParallelAcceptanceAttemptCoordinator.TryReadWorkspaceProbeBackoff(ConductorParallelAcceptanceCandidate.Create(
                kernel.GetGoal(candidate.GoalId), 0, ready.Projection.LandingPaths, ready.Projection.BranchRevision, ready.Projection.MainRevision), out _))
            .Select(candidate => candidate.GoalId.Value).ToHashSet(StringComparer.Ordinal));
        state.CohortEligible = state.CohortEligible
            .Where(goal => !interactionOnlyGoalIds.Contains(goal.Id.Value) && !workspaceProbeBackoffGoalIds.Contains(goal.Id.Value)).ToArray();
        state.ProductionCandidates = state.ProductionCandidates
            .Where(candidate => !interactionOnlyGoalIds.Contains(candidate.GoalId.Value) && !workspaceProbeBackoffGoalIds.Contains(candidate.GoalId.Value)).ToArray();
        state.InteractionOnlyPriorityGoal = SelectInteractionOnlyPriorityGoal(state, interactionOnlyGoalIds, kernel, driver, policy,
            tick, changedGoalLines, suppressNewAcceptanceAdmission);
        state.GroupedAdmissionOpen = !suppressNewAcceptanceAdmission() &&
            state.TransientRetryPriorityGoal is null && state.InteractionOnlyPriorityGoal is null &&
            trainAdmission.IsAdmitted &&
            driver.MergeTrainsEnabled &&
            state.CohortEligible.Length >= ConductorMergeTrainSelector.MinimumMembers &&
            !state.CohortEligible.Any(goal => IsAcceptanceEngineCircuitHoldRequired(
                goal.Status, _acceptanceEngineCircuit?.Read()));
    }

    private void AdmitMergeTrain(
        ParallelAcceptanceBatchState state,
        ConductorDriver driver, AgentOrchestratorKernel kernel,
        ConductorAutonomyPolicy policy, int tick, List<string> changedGoalLines)
    {
        var bound = MergeTrainOldestWaiterPassBound.For(this);
        if (state.GroupedAdmissionOpen &&
            state.CohortEligible.Length >= ConductorMergeTrainSelector.MinimumMembers &&
            bound.Select(
                state.ProductionCandidates,
                driver.ReadSuppressedGroupedPairs(),
                TrainIneligibleCriterionEvidenceGoalIds(state.CohortEligible, GateObligationSelectorPreAdmissionClassifier.HeldGoalIds(kernel, state.CohortEligible, state.ProductionCandidates, _workspace, line => RecordParallelAcceptanceProgress(line, changedGoalLines))), driver.ReadCohortAttributedMemberKeys(),
                driver.ReadTrainImplicatedMemberKeys(), tick, line => RecordParallelAcceptanceProgress(line, changedGoalLines)) is { } trainSelection &&
            GoalSchedulingOrder.GroupKeepsHead(trainSelection.Members.Select(member => member.GoalId.Value), state.CohortEligible, state.LiveAttemptGoalIds) &&
            bound.AllowsTrain(state.OldestWaiterObservation.Waiter, trainSelection.Members.Select(member => member.GoalId).ToArray(),
                state.OldestWaiterObservation.Waiter is { } waiter && state.Results.ContainsKey(waiter.Id.Value),
                state.CohortEligible.Any(goal => goal.Id == state.OldestWaiterObservation.Waiter?.Id), state.AcceptanceCensus,
                (goal, census) => EvaluateSoloAcceptanceAdmissibility(kernel, driver, goal, policy, census, state.ConfiguredAcceptanceWidth,
                    state.ActiveAttemptSlotIndexes, state.ActiveCandidates, state.LiveAttemptGoalIds, state.OldestWaiterObservation, false, tick),
                tick, line => RecordParallelAcceptanceProgress(line, changedGoalLines)))
        {
            var trainRun = driver.RunMergeTrain(
                trainSelection, state.CohortEligible, policy,
                onGateAdmitted: () => driver.RecordMergeTrainAdmissionFairness(trainSelection),
                runGateInBackground: true);
            state.GroupedAdmissionOpen = trainRun.MemberResults.Count > 0;
            PassedMergeTrainReceiptAdmission.RecordMergeTrainResult(
                trainSelection, state.CohortEligible, policy, trainRun,
                (goal, autonomy) => driver.TryHoldRepeatSoloAcceptance(goal, autonomy, bindingOnly: true),
                state.Results, tick, changedGoalLines, RecordParallelAcceptanceProgress);
            state.OrderedEligible = PassedAcceptanceReceiptReplay.ExcludeRefusedCarryHolds(state.OrderedEligible, state.Results);
            if (state.OldestWaiterObservation.Waiter is { } oldest && !state.OrderedEligible.Contains(oldest))
                state.OldestWaiterObservation = ObserveOldestParallelAcceptanceWaiter(state.OrderedEligible, state.LiveAttemptGoalIds);
            state.ActiveCohortCapacity = driver.GetActiveAcceptanceCohortCapacity();
            state.AcceptanceCensus = CaptureLiveAcceptanceCensus(
                state.LiveAttempts, state.ActiveAttemptIds, state.ActiveCohortCapacity, tick, changedGoalLines,
                blockAdmissionOnFailure: true, _mainFullVerification?.ActiveRunId);
            (state.CohortEligible, state.ProductionCandidates) = PassedMergeTrainReceiptAdmission.ExcludeResults(
                state.CohortEligible, state.ProductionCandidates, state.Results);
        }
    }

    private void AdmitPairCohort(
        ParallelAcceptanceBatchState state,
        AgentOrchestratorKernel kernel,
        ConductorDriver driver,
        ConductorAutonomyPolicy policy,
        int tick,
        List<string> changedGoalLines,
        HashSet<GoalId> changedGoalIds,
        Func<bool> suppressNewAcceptanceAdmission)
    {
        state.ForcedCohortPriority = null;
        var cohortAdmission = DecideLiveAcceptanceAdmission(state.AcceptanceCensus, state.ConfiguredAcceptanceWidth);
        if (PairTrainAdmission.CanAdmitTrain(state.GroupedAdmissionOpen, suppressNewAcceptanceAdmission(), cohortAdmission.IsAdmitted))
            AdmitMergeTrain(state, driver, kernel, policy, tick, changedGoalLines);
    }

    private void HoldCohortMembers(ParallelAcceptanceBatchState state, ConductorDriver driver,
        ConductorAutonomyPolicy policy,
        int tick,
        List<string> changedGoalLines) =>
        HoldCohortMembers(state.OrderedEligible, state.Results, driver, policy, tick, changedGoalLines);

    internal static void HoldCohortMembers(IReadOnlyList<Goal> orderedEligible,
        IDictionary<string, ParallelLandingOutcome> results, ConductorDriver driver,
        ConductorAutonomyPolicy policy, int tick, List<string> changedGoalLines)
    {
        driver.SweepCompletedCohortGateRuns();
        foreach (var goal in orderedEligible)
        {
            if (TrainEjectionReport.RefreshHold(driver, goal.Id, results) ||
                !TrainEjectionReport.TryGetCohortMemberHold(driver, goal.Id, out var cohortHoldDetail, sweep: false))
            {
                continue;
            }

            results[goal.Id.Value] = new ParallelLandingOutcome(
                CohortMemberHeld(goal, policy, cohortHoldDetail), SlotIndex: null);
            RecordParallelAcceptanceProgress(
                $"ACCEPTANCE_COHORT tick={tick} goal={goal.Id.Value[..8]} result=held {cohortHoldDetail}",
                changedGoalLines);
        }
    }

    private void AdmitSoloAcceptance(
        ParallelAcceptanceBatchState state,
        AgentOrchestratorKernel kernel,
        ConductorDriver driver,
        ConductorAutonomyPolicy policy,
        int tick,
        List<string> changedGoalLines,
        HashSet<GoalId> changedGoalIds,
        Func<bool> suppressNewAcceptanceAdmission)
    {
        if (suppressNewAcceptanceAdmission()) return;
        var oldestWaiter = state.OldestWaiterObservation.Waiter;
        var oldestServedThisTick = false;
        foreach (var goal in state.OrderedEligible)
        {
            // An inline completion can schedule a relaunch before the next candidate.
            if (suppressNewAcceptanceAdmission()) break;
            if (state.Results.ContainsKey(goal.Id.Value)) continue;
            try
            {
            var soloAdmission = ActivationAdmissionProbeScope.RecordSolo(goal.Id, GateObligationSelectorPreAdmissionClassifier.CheckSolo(
                kernel, driver, goal, policy, _workspace, SoloObligationOrchestratorDirectory, SoloObligationInventorySource,
                EvaluateSoloAcceptanceAdmissibility(kernel, driver, goal, policy, state.AcceptanceCensus, state.ConfiguredAcceptanceWidth,
                    state.ActiveAttemptSlotIndexes, state.ActiveCandidates, state.LiveAttemptGoalIds,
                    state.OldestWaiterObservation, oldestServedThisTick, tick, inspectObligationSelectors: false), changedGoalLines.Add));
            if (soloAdmission.Kind != SoloAcceptanceAdmissionKind.Admissible)
            {
                if (soloAdmission.Kind == SoloAcceptanceAdmissionKind.Capacity) state.DeferredByAdmission++;
                if (ApplySoloAcceptanceHold(soloAdmission, driver, goal, policy, changedGoalLines) is { } held)
                {
                    state.Results[goal.Id.Value] = held;
                }
                continue;
            }
            var candidate = soloAdmission.Candidate!;
            var stalledOldestBypass = soloAdmission.StalledOldestBypass;
            var documentationExclusionAdmission = ConductorDocumentationExclusionAdmission.Describe(
                candidate,
                state.ActiveCandidates,
                tick);
            var decision = driver.ParallelAcceptanceAttemptCoordinator.Evaluate(
                candidate,
                policy,
                driver.RunParallelLandingAcceptance);
            if (state.ForcedCohortPriority?.GoalId == goal.Id &&
                decision.Kind is ConductorParallelAcceptanceAttemptDecisionKind.Started or
                    ConductorParallelAcceptanceAttemptDecisionKind.Running or
                    ConductorParallelAcceptanceAttemptDecisionKind.Completed)
            {
                driver.ResetCohortFairness(goal.Id);
            }
            ConductorAcceptanceLeaseReceipts.Replay(driver, decision.Attempt, changedGoalLines);

            switch (decision.Kind)
            {
                case ConductorParallelAcceptanceAttemptDecisionKind.Started:
                    if (documentationExclusionAdmission is not null)
                    {
                        RecordParallelAcceptanceProgress(documentationExclusionAdmission, changedGoalLines);
                    }

                    if (decision.Attempt.Outcome != ConductorParallelAcceptanceAttemptOutcome.Running)
                    {
                        var terminalDecision = driver.ParallelAcceptanceAttemptCoordinator.Evaluate(
                            candidate,
                            policy,
                            driver.RunParallelLandingAcceptance);
                        ConductorAcceptanceLeaseReceipts.Replay(driver, terminalDecision.Attempt, changedGoalLines);
                        if (terminalDecision.Kind == ConductorParallelAcceptanceAttemptDecisionKind.Completed)
                        {
                            var terminalRun = terminalDecision.Run ?? ConductorParallelAcceptanceRunResult.Fault(
                                candidate,
                                new InvalidOperationException("Completed acceptance attempt had no run result."));
                            ReconcileParallelAcceptanceTerminalState(kernel, goal, terminalRun, terminalDecision.Attempt);
                            var terminalResult = CompleteParallelAcceptanceRun(
                                driver,
                                policy,
                                terminalRun,
                                terminalDecision.Attempt,
                                out var terminalEvidenceMutationLeaseHeld);
                            MarkParallelAcceptanceReconciledUnlessLeaseHeld(
                                driver,
                                terminalRun,
                                terminalEvidenceMutationLeaseHeld,
                                terminalDecision.Attempt);
                            oldestServedThisTick |= goal.Id == oldestWaiter?.Id;
                            RecordParallelAcceptanceFairnessGrant(goal.Id.Value, oldestWaiter?.Id.Value);
                            RecordParallelAcceptanceFairnessAdmission(
                                goal,
                                oldestWaiter,
                                stalledOldestBypass,
                                tick,
                                changedGoalLines);
                            state.Results[candidate.Goal.Id.Value] = new ParallelLandingOutcome(terminalResult, candidate.SlotIndex);
                            RecordParallelAcceptanceProgress(
                                AcceptanceLifecycleEventFormatter.Format(candidate.GoalPrefix, candidate.SlotIndex, AcceptanceRunDisposition(terminalRun), terminalDecision.Attempt.AttemptId, tick),
                                changedGoalLines);
                            break;
                        }

                        ReconcileParallelAcceptanceTerminalState(kernel, goal, terminalDecision.Attempt);
                        state.Results[candidate.Goal.Id.Value] = new ParallelLandingOutcome(
                            ParallelAcceptanceTerminal(driver, candidate, policy, terminalDecision.Attempt),
                            candidate.SlotIndex);
                        driver.ParallelAcceptanceAttemptCoordinator.MarkReconciled(terminalDecision.Attempt);
                        RecordParallelAcceptanceProgress(
                            AcceptanceLifecycleEventFormatter.Format(candidate.GoalPrefix, candidate.SlotIndex, AcceptanceAttemptOutcomeToken(terminalDecision.Attempt.Outcome), terminalDecision.Attempt.AttemptId, tick),
                            changedGoalLines);
                        break;
                    }

                    if (MarkParallelAcceptanceStarted(kernel, candidate.Goal, decision.Attempt, tick))
                    {
                        changedGoalIds.Add(candidate.Goal.Id);
                    }
                    ReserveParallelAcceptanceCandidate(
                        candidate,
                        decision.Attempt,
                        state.LiveAttempts,
                        state.ActiveCandidates,
                        state.ActiveAttemptIds,
                        state.ActiveAttemptSlotIndexes);
                    oldestServedThisTick |= goal.Id == oldestWaiter?.Id;
                    RecordParallelAcceptanceFairnessGrant(goal.Id.Value, oldestWaiter?.Id.Value);
                    RecordParallelAcceptanceFairnessAdmission(
                        goal,
                        oldestWaiter,
                        stalledOldestBypass,
                        tick,
                        changedGoalLines);
                    state.Results[candidate.Goal.Id.Value] = new ParallelLandingOutcome(
                        ParallelAcceptanceHeld(
                            candidate,
                            policy,
                            "acceptance verification running in background"),
                        candidate.SlotIndex);
                    RecordParallelAcceptanceProgress(
                        AcceptanceLifecycleEventFormatter.Format(candidate.GoalPrefix, candidate.SlotIndex, "started", decision.Attempt.AttemptId, tick),
                        changedGoalLines);
                    break;
                case ConductorParallelAcceptanceAttemptDecisionKind.Running:
                    if (MarkParallelAcceptanceStarted(kernel, candidate.Goal, decision.Attempt, tick))
                    {
                        changedGoalIds.Add(candidate.Goal.Id);
                    }
                    ReserveParallelAcceptanceCandidate(
                        candidate,
                        decision.Attempt,
                        state.LiveAttempts,
                        state.ActiveCandidates,
                        state.ActiveAttemptIds,
                        state.ActiveAttemptSlotIndexes);
                    oldestServedThisTick |= goal.Id == oldestWaiter?.Id;
                    RecordParallelAcceptanceFairnessGrant(goal.Id.Value, oldestWaiter?.Id.Value);
                    RecordParallelAcceptanceFairnessAdmission(
                        goal,
                        oldestWaiter,
                        stalledOldestBypass,
                        tick,
                        changedGoalLines);
                    state.Results[candidate.Goal.Id.Value] = new ParallelLandingOutcome(
                        ParallelAcceptanceHeld(
                            candidate,
                            policy,
                            "acceptance verification still running in background", ConductorHoldOwner.BackgroundAttempt),
                        candidate.SlotIndex);
                    RecordParallelAcceptanceProgress(
                        AcceptanceLifecycleEventFormatter.Format(candidate.GoalPrefix, candidate.SlotIndex, "running", decision.Attempt.AttemptId, tick),
                        changedGoalLines);
                    break;
                case ConductorParallelAcceptanceAttemptDecisionKind.Completed:
                    var run = decision.Run ?? ConductorParallelAcceptanceRunResult.Fault(
                        candidate,
                        new InvalidOperationException("Completed acceptance attempt had no run result."));
                    PassedAcceptanceReceiptReplay.ReconcileUnlessReused(kernel, goal, run, decision.Attempt);
                    var result = CompleteParallelAcceptanceRun(
                        driver,
                        policy,
                        run,
                        decision.Attempt,
                        out var evidenceMutationLeaseHeld);
                    MarkParallelAcceptanceReconciledUnlessLeaseHeld(
                        driver,
                        run,
                        evidenceMutationLeaseHeld,
                        decision.Attempt);
                    oldestServedThisTick |= goal.Id == oldestWaiter?.Id;
                    RecordParallelAcceptanceFairnessGrant(goal.Id.Value, oldestWaiter?.Id.Value);
                    RecordParallelAcceptanceFairnessAdmission(
                        goal,
                        oldestWaiter,
                        stalledOldestBypass,
                        tick,
                        changedGoalLines);
                    state.Results[candidate.Goal.Id.Value] = new ParallelLandingOutcome(result, candidate.SlotIndex);
                    RecordParallelAcceptanceProgress(
                        AcceptanceLifecycleEventFormatter.Format(candidate.GoalPrefix, candidate.SlotIndex, AcceptanceRunDisposition(run), decision.Attempt.AttemptId, tick),
                        changedGoalLines);
                    break;
                case ConductorParallelAcceptanceAttemptDecisionKind.TerminalWithoutRun:
                    ReconcileParallelAcceptanceTerminalState(kernel, goal, decision.Attempt);
                    state.Results[candidate.Goal.Id.Value] = new ParallelLandingOutcome(
                        ParallelAcceptanceTerminal(driver, candidate, policy, decision.Attempt),
                        candidate.SlotIndex);
                    driver.ParallelAcceptanceAttemptCoordinator.MarkReconciled(decision.Attempt);
                    RecordParallelAcceptanceProgress(
                        AcceptanceLifecycleEventFormatter.Format(candidate.GoalPrefix, candidate.SlotIndex, AcceptanceAttemptOutcomeToken(decision.Attempt.Outcome), decision.Attempt.AttemptId, tick),
                        changedGoalLines);
                    break;
            }
            state.AcceptanceCensus = CaptureLiveAcceptanceCensus(
                state.LiveAttempts,
                state.ActiveAttemptIds,
                state.ActiveCohortCapacity,
                tick,
                changedGoalLines,
                blockAdmissionOnFailure: true, _mainFullVerification?.ActiveRunId);
            }
            catch (AcceptanceArtifactWriterLeaseBusyException ex)
            {
                state.Results[goal.Id.Value] = new ParallelLandingOutcome(
                    ParallelAcceptanceHeld(
                        goal,
                        policy,
                        $"acceptance artifact writer busy; retry on next conduct tick. {ex.Message}"),
                    null);
                RecordParallelAcceptanceProgress(
                    $"ADMISSION tick={tick} result=deferred reason=acceptance-artifact-writer-busy goal={goal.Id.Value[..8]} detail={SanitizeReason(ex.Message)}",
                    changedGoalLines);
            }
            catch (Exception ex)
            {
                var reason = BoundSingleLine(
                    $"parallel acceptance fault isolated before goal advance: {ex.GetType().Name}: {ex.Message}");
                state.Results[goal.Id.Value] = new ParallelLandingOutcome(
                    EscalateParallelAcceptanceSafely(driver, goal, policy, reason),
                    null);
                RecordParallelAcceptanceProgress(
                    $"ADMISSION tick={tick} result=escalated reason=parallel-acceptance-fault goal={goal.Id.Value[..8]} detail={SanitizeReason(reason)}",
                    changedGoalLines);
            }
            finally { TrainEjectionReport.RefreshHold(driver, goal.Id, state.Results); }
            if (suppressNewAcceptanceAdmission()) break;
        }
    }
    private void SummarizeAcceptanceDeferrals(
        ParallelAcceptanceBatchState state,
        int tick,
        List<string> changedGoalLines)
    {
        if (state.DeferredByAdmission > 0)
        {
            RecordParallelAcceptanceProgress(
                $"ADMISSION tick={tick} result=deferred reason=parallel-acceptance-slot-cap cap={state.ConfiguredAcceptanceWidth} deferred={state.DeferredByAdmission}",
                changedGoalLines);
        }
    }
}
