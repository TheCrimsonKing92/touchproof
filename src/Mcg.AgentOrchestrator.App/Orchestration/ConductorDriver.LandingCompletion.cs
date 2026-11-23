using System.Security.Cryptography;
using System.Text;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorDriver
{
    private readonly SoloBaselineRepairCoordinator? _soloBaselineRepair;

    private SoloAcceptanceConsumptionContext? ReadSoloAcceptanceContext(Goal goal) =>
        SoloAcceptanceContextReader is { } reader ? reader(goal) :
            SoloAcceptanceFailureHandler.ReadContext(goal, _executionDirectory, ReadCurrentCohortAttributionRevisions(goal));

    private SoloAcceptanceFailureHandler SoloAcceptanceFailure => new(
        () => _cohortKernel ?? _conductorTickKernel, _recordAcceptanceFailure, _utcNow, ReadSoloAcceptanceContext,
        _recordCriterionRetryFeedback, RouteAttributedFailure, RecordEscalation,
        goal => _parallelAcceptanceAttemptCoordinator.TryReadLatest(goal.Id.Value),
        (goal, route) => FailureDiagnosis?.Enrich(goal, route) ?? route,
        (goal, prefix, policy, acceptance) => _soloBaselineRepair?.TryAdvance(goal, prefix, policy, acceptance));

    internal LandingEscalationRecheckResult RecheckPreLandingRebaseConflict(Goal goal)
        => _recheckPreLandingRebaseConflict(goal);

    private ConductorAdvanceResult? RebaseOrRetire(
        Goal goal,
        string goalPrefix,
        ConductorAutonomyPolicy policy,
        string phase,
        bool applySideEffects,
        out ConductorParallelAcceptanceEarlyOutcome? earlyOutcome,
        out GoalWorktreeRebaseStatus rebaseStatus)
    {
        earlyOutcome = null;
        var rebase = _rebaseOntoMain(goal);
        rebaseStatus = rebase.Status;

        // No retry here, deliberately. An earlier version of this method retried a non-conflict failure once,
        // on the theory that those failures were transient races against main advancing. That theory was
        // WRONG: the nine occurrences that motivated it were a deterministic git exit 128, because the
        // pre-landing rebase runs inside the hermetic acceptance child, which had no git identity and so
        // could not create the commits a rebase replays. Retrying a deterministic failure cannot help, and it
        // doubled the cost of every genuine one. The identity fix belongs in the environment, not here.
        return CreateLandingRebaseOutcome(goal, goalPrefix, policy, phase, applySideEffects, rebase, out earlyOutcome);
    }

    private ConductorAdvanceResult CompleteLandingAfterAcceptance(
        Goal goal,
        string goalPrefix,
        ConductorAutonomyPolicy policy,
        AcceptanceVerificationSummary acceptance)
    {
        goal = GetCurrentGoal(goal);
        if (_soloBaselineRepair?.ObserveAcceptedPair(goal, goalPrefix, policy, acceptance) is { } repairHold)
            return repairHold;
        if (acceptance.Passed && MultiRepositoryGoal.Applies(goal))
            return ExecuteRepositoryAcceptance(goal, goalPrefix, policy, GoalLifecycle.ResolveState(goal));
        var decision = LandingGoalStateGuardPolicy.Evaluate(new(null, goal.LifecycleStatus,
            goal.AcceptanceFailureDeferredForRetry, acceptance.Passed));
        if (decision.Action == LandingGoalStateGuardAction.Hold)
            return MakeResult(goal.Id.Value, goalPrefix, policy,
                new ConductorAdvanceOutcome.Held(GoalLifecycleState.WorkspaceReady, decision.Reason) { Decision = decision.ToRecord() });

        // Terminal replay returns before census to avoid counting the same failed gate twice.
        // Census every new gate completion, including apparatus and unattributable early returns.
        // Lazy keeps the changed-path git call at one per advance and off paths that never need it.
        var landingFileScopes = new Lazy<IReadOnlyList<string>>(() => _getLandingFileScopes(goal));
        var apparatusRedReading = _apparatusRedGate?.RecordGateCompletion(goal, acceptance, landingFileScopes);
        if (TryDisposeWithinAttemptRerunApparatus(goal, goalPrefix, policy, acceptance) is { } rerunApparatus)
        {
            return rerunApparatus;
        }
        if (IsEnvironmentalApparatusAcceptanceRun(acceptance))
        {
            var failedChecks = acceptance.FailedChecks is { Count: > 0 }
                ? acceptance.FailedChecks
                : acceptance.RequiredUnmetCriteria.Select(check => check.Name).ToArray();
            var observedHeads = _resolveAcceptanceHeads(goal);
            var branchHeadSha = acceptance.BranchHeadSha ?? observedHeads.BranchHeadSha;
            var mainHeadSha = acceptance.MainHeadSha ?? observedHeads.MainHeadSha;
            _recordAcceptanceFailure(
                goal,
                failedChecks,
                branchHeadSha,
                mainHeadSha,
                acceptance.CheckAttributions,
                acceptance.BaselineAttestation);
            return CreateApparatusCompletionHold(goal, goalPrefix, policy, acceptance, branchHeadSha, mainHeadSha);
        }

        if (!acceptance.Passed && (acceptance.SoloFailureReceipt is not null || acceptance.RequiresSoloFailureReceipt))
        {
            // Earlier inherited failures can make a post-repair candidate failure look
            // recurrent. Let the repair coordinator validate the new receipt first.
            if (_soloBaselineRepair is not null && acceptance.SoloFailureReceipt is not null &&
                (_soloBaselineRepair.HasObligations(goal) || acceptance.RequiredUnmetCriteria.Any(check =>
                    check.FailingTestAttributions is not { Count: > 0 } attributions ||
                    attributions.Any(test => test.Origin is AcceptanceTestFailureOrigin.Inherited or AcceptanceTestFailureOrigin.Unattributed))) &&
                ReadSoloAcceptanceContext(goal) is not null)
                return SoloAcceptanceFailure.Complete(goal, goalPrefix, policy, acceptance);

            // Prepared solo attribution can prove every failure is inherited. Keep
            // the existing excluded hold and main-suspect evidence before routing
            // actionable findings through the solo receipt consumer.
            var retryDisposition = AcceptanceRetryClassifier.Classify(acceptance.RequiredUnmetCriteria);
            var attemptedAllFlakyDisposition = false;
            if (acceptance.RequiredUnmetCriteria.Count > 0 && retryDisposition.ActionableCriteria.Count == 0 &&
                acceptance.SoloFailureReceipt is null && TryDisposeExcludedAcceptanceFailures(
                    goal, goalPrefix, policy, acceptance, apparatusRedReading,
                    ref retryDisposition, out attemptedAllFlakyDisposition) is { } excludedResult)
                return excludedResult;

            // Attempt IDs also identify legacy and background completions. Only
            // producer-owned solo evidence selects this path; keep that obligation
            // when a captured receipt is subsequently lost during transport.
            // Preserve the existing apparatus disposition before admitting solo evidence.
            if (!attemptedAllFlakyDisposition &&
                TryDisposeApparatusRed(goal, goalPrefix, policy, acceptance, apparatusRedReading) is { } soloApparatus)
                return soloApparatus;

            // A solo test finding must use the evidence route, never the generic
            // criterion feedback retry (which cannot establish attribution).
            if (acceptance.SoloFailureReceipt is not null && retryDisposition.ActionableCriteria.Count == 0)
                TryRecordSoloMainSuspect(goal, goalPrefix, acceptance, retryDisposition.ExcludedFailures,
                    acceptance.BranchHeadSha, acceptance.MainHeadSha, apparatusRedReading?.ChangedPaths);
            return SoloAcceptanceFailure.Complete(goal, goalPrefix, policy, acceptance);
        }

        if (!acceptance.Passed && acceptance.RequiredUnmetCriteria.Count == 0)
        {
            var failedChecks = acceptance.FailedChecks ?? [];
            var timedOut = failedChecks.Any(IsBlockingTimeoutCheck);
            if (!timedOut && acceptance.FailedChecks is { Count: > 0 })
            {
                _recordAcceptanceFailure(
                    goal,
                    failedChecks,
                    acceptance.BranchHeadSha,
                    acceptance.MainHeadSha,
                    acceptance.CheckAttributions,
                    acceptance.BaselineAttestation);
            }

            return CreateFailedAcceptanceCompletion(goal, goalPrefix, policy, acceptance, timedOut);
        }

        if (acceptance.Passed)
        {
            goal = GetCurrentGoal(goal);
            _clearAcceptanceFailure(goal);
            var evidenceHold = AcceptanceCriterionEvidence.RecordAndCreateHold(
                goal, acceptance, _cohortKernel ?? _conductorTickKernel, _integrationBranch, _executionDirectory, _resolveAcceptanceHeads);
            if (evidenceHold is not null)
            {
                return MakeResult(goal.Id.Value, goalPrefix, policy, AttachCriterionEvidenceCompletionDecision(acceptance, evidenceHold));
            }
        }

        if (acceptance.RequiredUnmetCriteria.Count > 0)
        {
            if (TryHoldForOwnerReview(goal, goalPrefix, policy, acceptance) is { } ownerReviewHold)
                return ownerReviewHold;

            var retryDisposition = AcceptanceRetryClassifier.Classify(acceptance.RequiredUnmetCriteria);
            var attemptedAllFlakyDisposition = false;
            if (retryDisposition.ActionableCriteria.Count == 0 &&
                TryDisposeExcludedAcceptanceFailures(
                    goal, goalPrefix, policy, acceptance, apparatusRedReading,
                    ref retryDisposition, out attemptedAllFlakyDisposition) is { } excludedResult)
                return excludedResult;

            if (!attemptedAllFlakyDisposition &&
                TryDisposeApparatusRed(goal, goalPrefix, policy, acceptance, apparatusRedReading) is { } apparatusRed)
                return apparatusRed;

            if (retryDisposition.ExcludedFailures.Count > 0)
            {
                var observedHeads = _resolveAcceptanceHeads(goal);
                _recordAcceptanceFailure(
                    goal,
                    acceptance.FailedChecks is { Count: > 0 }
                        ? acceptance.FailedChecks
                        : acceptance.RequiredUnmetCriteria.Select(check => check.Name).ToArray(),
                    acceptance.BranchHeadSha ?? observedHeads.BranchHeadSha,
                    acceptance.MainHeadSha ?? observedHeads.MainHeadSha,
                    acceptance.CheckAttributions,
                    acceptance.BaselineAttestation);
            }

            var criteria = FormatUnmetCriteria(retryDisposition.ActionableCriteria);
            IReadOnlyList<string> attributionLines;
            try
            {
                attributionLines = _apparatusRedGate?.FormatFailingClassAttribution(
                    goal, acceptance.MainHeadSha ?? _resolveAcceptanceHeads(goal).MainHeadSha,
                    apparatusRedReading, retryDisposition.ActionableCriteria) ?? [];
            }
            catch (Exception)
            {
                // Attribution is advisory; even a head-resolution failure must preserve routing.
                attributionLines = [];
            }
            if (attributionLines.Count > 0)
                criteria += "; " + string.Join("; ", attributionLines);
            if (TryDisposeTimeoutOnlyGateFailure(goal, goalPrefix, policy, acceptance) is { } timeoutResult)
                return timeoutResult;

            var task = SelectTaskForCriterionRetry(goal);
            if (task is null)
            {
                return CreateUnmetCriteriaCompletion(goal, goalPrefix, policy, acceptance, criteria, retryTaskAvailable: false);
            }

            if (goal.AutomaticAcceptanceRetryCount < policy.MaxCriterionRetries)
            {
                var failureRecordedAt = _utcNow();
                var retryFeedback = FailedLaneSelectedClasses.AppendFeedback(goal.Id, acceptance, _parallelAcceptanceAttemptCoordinator,
                    [.. FormatCriterionRetryFeedback(retryDisposition.ActionableCriteria), .. attributionLines]);
                var retryCount = _recordCriterionRetryFeedback(
                    goal.Id,
                    task.Id,
                    retryFeedback);
                var retryMessage = $"Acceptance criteria unmet; retrying task with feedback (attempt {retryCount}/{policy.MaxCriterionRetries}): " +
                    string.Join(Environment.NewLine, retryFeedback);
                _retryTask(goal.Id, task.Id, retryMessage, null, RetryCause.CriterionEvidenceOwnerMismatch);
                var retryGoal = GetCurrentGoal(goal);
                var retryTask = retryGoal.FindTask(task.Id);
                OperatorRoutingNoteLedger.OpenHold(retryTask, failureRecordedAt,
                    policy.AutomaticRetryOperatorRoutingHoldSeconds, acceptance.AttemptId, retryFeedback);
                if (policy.AutomaticRetryOperatorRoutingHoldSeconds > 0)
                    _recordTaskNote(goal.Id, task.Id, AcceptanceRetryRoutingHoldGate.Describe(retryTask, _utcNow()));
                return MakeResult(goal.Id.Value, goalPrefix, policy,
                    new ConductorAdvanceOutcome.Executed(GoalLifecycleState.Verified, retryMessage));
            }

            return CreateUnmetCriteriaCompletion(goal, goalPrefix, policy, acceptance, criteria, retryTaskAvailable: true);
        }

        foreach (var task in goal.Tasks)
        {
            _clearCriterionRetryFeedback(goal.Id, task.Id);
        }

        // Gate 3: land via integration branch (the branch is already rebased onto main by Gate 1).
        var mutationBlockReason = LandingMutationBlocker?.Invoke() is { } blocked && !string.IsNullOrWhiteSpace(blocked) ? blocked : InFlightTrainLandingHold.Describe(goal.Id, GetActiveAcceptanceCohortCapacity());
        if (!string.IsNullOrWhiteSpace(mutationBlockReason))
        {
            return CreateLandingMutationCompletionHold(goal, goalPrefix, policy, acceptance, mutationBlockReason);
        }

        var landResult = _land(goal, policy);
        if (CreateLandingResultCompletionOutcome(goal, goalPrefix, policy, acceptance, landResult) is { } landingOutcome)
        {
            return landingOutcome;
        }

        RecordAdvisoryAcceptanceNotes(goal, acceptance.AdvisoryUnmetCriteria);

        if (landResult.MainAdvanced)
        {
            // Schedule any required generation handoff before fallible advisory/post-landing work.
            // Main has already advanced, so losing this receipt would permanently miss the relaunch.
            SuccessfulLandingSink?.Invoke(new ConductorLandingReceipt(
                goal.Id.Value,
                landResult.ChangedFiles ?? landingFileScopes.Value,
                landResult.MergeCommitSha, acceptance.AttemptId, PreviousMainSha: acceptance.MainHeadSha));

            // Gate 4: advisory semantic acceptance runs only after deterministic acceptance and
            // successful landing. It records judge receipts for observability but never gates landing.
            _runAdvisorySemanticAcceptance(goal, acceptance);
            _afterSuccessfulLanding(goal, landResult);
        }

        return MakeResult(goal.Id.Value, goalPrefix, policy,
            new ConductorAdvanceOutcome.Executed(GoalLifecycleState.Verified, $"Landed: {landResult.Message}"));
    }

    private ConductorAdvanceResult ExecuteRecord(Goal goal, string goalPrefix, ConductorAutonomyPolicy policy)
    {
        _record(goal);
        return MakeResult(goal.Id.Value, goalPrefix, policy,
            new ConductorAdvanceOutcome.Executed(GoalLifecycleState.Merged, "Recorded to dogfood log"));
    }

    private ConductorAdvanceResult ExecuteCleanup(Goal goal, string goalPrefix, ConductorAutonomyPolicy policy)
    {
        GoalWorktreeRemoveResult cleanup;
        try
        {
            cleanup = _cleanup(goal);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            _completeGoal(goal);
            return MakeResult(goal.Id.Value, goalPrefix, policy,
                new ConductorAdvanceOutcome.Executed(
                    GoalLifecycleState.Recorded,
                    $"Workspace cleanup deferred after removal failure; retry later. {ex.Message}"));
        }

        _completeGoal(goal);
        return MakeResult(goal.Id.Value, goalPrefix, policy,
            new ConductorAdvanceOutcome.Executed(GoalLifecycleState.Recorded, cleanup.Message));
    }

    private ConductorAdvanceResult Escalate(
        Goal goal,
        string goalPrefix,
        ConductorAutonomyPolicy policy,
        GoalLifecycleState state,
        string reason, ConductorEscalationKind? kind = null)
    {
        RecordEscalation(goal, state, reason);
        return MakeResult(goal.Id.Value, goalPrefix, policy,
            new ConductorAdvanceOutcome.Escalated(state, reason, kind));
    }

    private static ConductorAdvanceResult MakeResult(
        string goalId,
        string goalPrefix,
        ConductorAutonomyPolicy policy,
        ConductorAdvanceOutcome outcome) =>
        new(goalId, goalPrefix, policy.Name, outcome);

    private static TaskSpec? SelectTaskForCriterionRetry(Goal goal) =>
        goal.Tasks.LastOrDefault(task => task.Status == WorkTaskStatus.Completed && task.RequiredRole == AgentRole.Developer) ??
        goal.Tasks.LastOrDefault(task => task.Status == WorkTaskStatus.Completed);

    private void RecordAdvisoryAcceptanceNotes(Goal goal, IReadOnlyList<AcceptanceCheckResult> advisoryCriteria)
    {
        if (advisoryCriteria.Count > 0 && SelectTaskForCriterionRetry(goal) is { } task)
            _recordTaskNote(goal.Id, task.Id,
                $"Advisory acceptance criteria observed during landing (non-gating): {FormatUnmetCriteria(advisoryCriteria)}");
    }

    private static string FormatUnmetCriteria(IReadOnlyList<AcceptanceCheckResult> criteria) =>
        string.Join("; ", criteria.Select(FormatUnmetCriterion));

    internal string? LaneTimeoutConductEventLogPathOverride { get; set; }

    private ConductorAdvanceResult? TryDisposeTimeoutOnlyGateFailure(
        Goal goal, string goalPrefix, ConductorAutonomyPolicy policy, AcceptanceVerificationSummary acceptance)
    {
        var (branch, main) = (acceptance.BranchHeadSha, acceptance.MainHeadSha);
        if (acceptance.Passed || _executionDirectory is null || acceptance.AttemptId is not { Length: > 0 } attemptId ||
            string.IsNullOrWhiteSpace(branch) || string.IsNullOrWhiteSpace(main) ||
            !acceptance.UnmetCriteria.Any(check => !check.Passed && !check.Advisory && IsBlockingTimeoutCheck(check.Name)))
            return null;

        const string decisionStage = "acceptance-timeout-recovery";
        const string timeout = "blocked:timeout";
        GoalOperationJournalEntry[] attempts;
        try
        {
            attempts = GoalOperationJournal.ReadStrict(_executionDirectory, goal.Id).Entries
                .Where(entry => entry.AcceptanceOutcome is "passed" or "gate-passed" or "failed" or "aborted:state-guard" ||
                    entry.AcceptanceOutcome?.StartsWith("blocked:", StringComparison.Ordinal) == true).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or FormatException)
        { return null; }
        if (attempts.LastOrDefault() is not { AcceptanceOutcome: timeout, FailedCheckNames: { } recorded } current ||
            !current.HasCandidate(branch, main) ||
            !recorded.ToHashSet(StringComparer.Ordinal).SetEquals(acceptance.RequiredUnmetCriteria.Select(check => check.Name)))
            return null;
        var attemptKey = LaneTimeoutEscalationDetail.TimeoutGateAttemptKey(current);
        if (SelectTaskForCriterionRetry(goal) is { } noteTask)
            _recordTaskNote(goal.Id, noteTask.Id, $"ACCEPTANCE_TIMEOUT_ATTEMPT: {attemptKey}{attemptId}");
        if (acceptance.FailedChecks?.Any(name => !IsBlockingTimeoutCheck(name)) == true ||
            acceptance.UnmetCriteria.Any(check =>
                (!check.Passed && LaneTimeoutEscalationDetail.TimeoutLane(check.Name) is null) ||
                check.FailingTestIdentities?.Any(identity => !string.IsNullOrWhiteSpace(identity)) == true ||
                check.FailingTestAttributions?.Any(attribution => !string.IsNullOrWhiteSpace(attribution.TestIdentity)) == true))
            return null;

        var checks = acceptance.UnmetCriteria.Where(check => !check.Passed).Select(check => check.Name).ToArray();
        if (attempts.Length > 1 && attempts[^2] is { AcceptanceOutcome: timeout, FailedCheckNames: { } priorChecks } prior &&
            prior.HasCandidate(branch, main) &&
            checks.Join(priorChecks, LaneTimeoutEscalationDetail.TimeoutLane, LaneTimeoutEscalationDetail.TimeoutLane,
                (check, previous) => (Current: check, Prior: previous),
                StringComparer.OrdinalIgnoreCase).Distinct().ToArray() is { Length: > 0 } repeated)
        {
            var priorKey = LaneTimeoutEscalationDetail.TimeoutGateAttemptKey(prior);
            var priorId = goal.Timeline.LastOrDefault(evt => evt.Kind == ProgressKind.TaskNote &&
                evt.Message.StartsWith("ACCEPTANCE_TIMEOUT_", StringComparison.Ordinal) && evt.Message.Contains(priorKey, StringComparison.Ordinal))
                ?.Message.Split(priorKey, 2)[1].Split(' ', 2)[0];
            if (priorId is null or "" or "unknown") return null;
            var details = repeated.Select(pair => $"attempt {priorId} ({pair.Prior}), attempt {attemptId} ({pair.Current})");
            var timeoutLaneDetail = LaneTimeoutEscalationDetail.Format(acceptance, repeated.Select(pair => pair.Current), current,
                _executionDirectory, LaneTimeoutConductEventLogPathOverride ?? _cohortWorkspace?.ConductEventsLogPath);
            var escalationReason = $"ACCEPTANCE_TIMEOUT_CONSECUTIVE: candidate {branch}/{main} timed out on consecutive gate attempts. " +
                string.Join("; ", details) + "\n" + timeoutLaneDetail;
            var state = GoalLifecycle.ResolveState(goal);
            RecordEscalation(goal, state, escalationReason);
            return MakeResult(goal.Id.Value, goalPrefix, policy, new ConductorAdvanceOutcome.Escalated(
                state, escalationReason, ConductorEscalationKind.BackgroundAcceptanceFailed)
                { Decision = new(decisionStage, "Escalate", 1, "consecutive-same-lane-timeout", escalationReason,
                    [new("branchSha", branch), new("mainSha", main), new("previousAttemptId", priorId), new("attemptId", attemptId),
                        new("timeoutLaneDetail", timeoutLaneDetail)]) });
        }
        if (attempts.SkipLast(1).Any(entry => entry.AcceptanceOutcome == timeout &&
            (!entry.HasCandidatePair || entry.HasCandidate(branch, main))))
            return null;
        var reason = $"ACCEPTANCE_TIMEOUT_REGATE: {attemptKey}{attemptId} ({string.Join(", ", checks)}). " +
            "Re-gating the unchanged candidate on the next conduct tick.";
        RestoreVerifiedAfterAcceptanceClassification(goal, reason);
        if (SelectTaskForCriterionRetry(goal) is { } task)
            _recordTaskNote(goal.Id, task.Id, reason);
        return MakeResult(goal.Id.Value, goalPrefix, policy, new ConductorAdvanceOutcome.Held(
            GoalLifecycleState.Verified, reason, StableIdentity: $"acceptance-timeout-regate:{branch}:{main}")
            { Decision = new(decisionStage, "Hold", 2, "first-timeout-on-candidate", reason,
                [new("branchSha", branch), new("mainSha", main), new("attemptId", attemptId)]) });
    }

    internal static string[] InferRecordedFileScopes(Goal goal)
    {
        var scopes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var task in goal.Tasks)
        {
            var text = $"{goal.Objective}\n{task.Description}\n{task.VerificationPlan}";
            foreach (var token in text.Split([' ', '\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var normalized = token.Replace('\\', '/').TrimEnd('.', ',', ';', ':', ')', ']');
                if (normalized.Contains('/') && !string.IsNullOrWhiteSpace(Path.GetExtension(normalized)))
                {
                    scopes.Add(normalized.TrimStart('/'));
                }
            }
        }

        return scopes.Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static string BuildUnattributableAcceptanceIdentity(
        string? branchHeadSha,
        string? mainHeadSha,
        IReadOnlyList<ExcludedAcceptanceFailure> excludedFailures)
    {
        var fingerprintSource = string.Join(
            "|",
            excludedFailures
                .OrderBy(failure => failure.Identity, StringComparer.Ordinal)
                .ThenBy(failure => failure.Kind)
                .Select(failure => $"{failure.Kind}:{failure.Identity}"));
        var fingerprint = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(fingerprintSource))).ToLowerInvariant()[..16];
        return $"acceptance-unattributable:{branchHeadSha ?? "unknown"}:{mainHeadSha ?? "unknown"}:{fingerprint}";
    }

    private static string[] FormatCriterionRetryFeedback(IReadOnlyList<AcceptanceCheckResult> criteria)
    {
        var concreteEvidence = ExtractConcreteRetryEvidence(criteria);
        var summary = criteria.Select(FormatUnmetCriterion);
        return concreteEvidence.Count == 0
            ? summary.ToArray()
            : ["Concrete acceptance failure evidence:", .. concreteEvidence, "Acceptance criteria summary:", .. summary];
    }

    private static List<string> ExtractConcreteRetryEvidence(IReadOnlyList<AcceptanceCheckResult> criteria)
    {
        var evidence = new List<string>();
        var budget = new AcceptanceRetryEvidenceBudget();
        var remainingEvidenceEntries = MaxCriterionRetryEvidenceLines;
        foreach (var criterion in criteria.Where(criterion => !criterion.Passed))
        {
            var outputEvidence = AcceptanceRetryOutputEvidence.ExtractConcreteOutputEvidence(criterion).ToArray();
            var allowedFailingIdentities = criterion.FailingTestIdentities?.
                Where(identity => !string.IsNullOrWhiteSpace(identity))
                .ToHashSet(StringComparer.Ordinal);
            var testResultPaths = criterion.TestResultPaths?
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray() ?? [];

            if (outputEvidence.Length == 0 && testResultPaths.Length == 0)
            {
                continue;
            }

            evidence.Add(FormatFailedCheckEvidence(criterion));

            if (testResultPaths.Length == 0)
            {
                AcceptanceRetryOutputEvidence.AppendBounded(evidence, outputEvidence, ref remainingEvidenceEntries);
                evidence.Add($"detail unavailable: no TRX path was recorded for partition \"{criterion.Name}\"");
                continue;
            }

            var fallbackAdded = false;
            foreach (var testResultPath in testResultPaths)
            {
                var receipt = AcceptanceTrxFailureReader.Read(testResultPath);
                var receiptLineIndex = evidence.Count;
                var readable = receipt.Status == AcceptanceTrxReadStatus.Readable;
                if (readable)
                    evidence.Add($"TRX receipt for partition \"{criterion.Name}\": {receipt.Path}");
                var attributableFailures = allowedFailingIdentities is null or { Count: 0 }
                    ? receipt.Failures
                    : receipt.Failures
                        .Where(failure => allowedFailingIdentities.Contains(failure.TestName))
                        .ToArray();
                if (readable && attributableFailures.Count > 0)
                {
                    budget.AppendFailures(evidence, criterion.Name, receipt.Path, receiptLineIndex,
                        attributableFailures, ref remainingEvidenceEntries);
                    continue;
                }

                if (!fallbackAdded)
                {
                    AcceptanceRetryOutputEvidence.AppendBounded(evidence, outputEvidence, ref remainingEvidenceEntries);
                    fallbackAdded = true;
                }
                var partition = $"partition \"{criterion.Name}\"";
                var detail = string.IsNullOrWhiteSpace(receipt.Detail) ? string.Empty : $" ({receipt.Detail})";
                var missingResults = receipt.Failures.Count == 0 ? "non-passing" : "attributable non-passing";
                evidence.Add(!readable
                    ? $"detail unavailable: {DescribeTrxReadFailure(receipt.Status)} for {partition} at {receipt.Path}{detail}"
                    : $"detail unavailable: readable TRX for {partition} contained no {missingResults} results at {receipt.Path}");
            }
            AppendAssemblyCleanupStderrEvidence(evidence, criterion, testResultPaths);
        }

        return budget.Complete(evidence);
    }

    private static string DescribeTrxReadFailure(AcceptanceTrxReadStatus status) => status switch
    {
        AcceptanceTrxReadStatus.Missing => "no TRX exists",
        AcceptanceTrxReadStatus.Unparseable => "TRX is unparseable",
        AcceptanceTrxReadStatus.Unreadable => "TRX is unreadable",
        _ => "no readable TRX"
    };

    private static string FormatFailedCheckEvidence(AcceptanceCheckResult criterion) =>
        $"failed check: {criterion.Name} (exit code {criterion.ExitCode})";

    private static string FormatUnmetCriterion(AcceptanceCheckResult criterion)
    {
        var summary = string.IsNullOrWhiteSpace(criterion.ResultSummary)
            ? criterion.OutputTail
            : criterion.ResultSummary;
        return string.IsNullOrWhiteSpace(summary)
            ? criterion.Name
            : $"{criterion.Name}: {summary.Trim()}";
    }
}
