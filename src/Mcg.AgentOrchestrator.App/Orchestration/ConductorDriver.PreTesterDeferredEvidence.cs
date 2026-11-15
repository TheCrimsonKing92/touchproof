using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorDriver
{
    internal Func<Goal, IReadOnlyList<string>, IReadOnlyDictionary<string, IReadOnlyList<string>>>? PreTesterFailureHistory { get; set; }
    internal Func<string, string>? PreTesterMainRevision { get; set; }
    internal PreTesterEvidenceRecorder? PreTesterRecorder { get; set; }
    private readonly PreTesterChangedFileGuardClasses _preTesterChangedFileGuards = new();

    private void RecordPreTesterDeferredEvidenceExit(
        Goal goal, TaskSpec developer, string testsField, PreTesterDeferredEvidenceExit exit,
        string? priorOutcome, IReadOnlyList<string> notRun) =>
        _recordTaskNote(goal.Id, developer.Id,
            PreTesterDeferredEvidenceExitNote.FormatSkipped(testsField, exit, priorOutcome, notRun));

    private void RecordPreTesterDeferredEvidenceContinuation(
        Goal goal, TaskSpec developer, string candidateSha, string supersededReceiptId, string freshReceiptId) =>
        _recordTaskNote(goal.Id, developer.Id,
            PreTesterDeferredEvidenceExitNote.FormatContinuation(candidateSha, supersededReceiptId, freshReceiptId));

    private PreTesterOutOfDiffRerun OutOfDiffRerun(string worktree) => new(worktree, _executionDirectory,
        _getLandingFileScopes, PreTesterFailureHistory ?? ((goal, identities) => CrossGoalTestFailureHistory.Read(
            _cohortWorkspace, (_cohortKernel ?? _conductorTickKernel)?.Goals ?? [], goal, identities, _utcNow())),
        TryReconcileFocusedEvidenceAttempt, _recordFindingEvidenceRequest, _recordFindingEvidenceRun,
        CreateFindingEvidenceBatchId, CreateFindingEvidenceReceiptId);

    private bool TryRunPreTesterDeferredEvidence(
        Goal goal,
        TaskSpec developer,
        TaskSpec tester,
        string worktreePath,
        string? candidateSha,
        string goalPrefix,
        ConductorAutonomyPolicy policy,
        GoalLifecycleState fromState,
        out ConductorAdvanceResult result)
    {
        result = default!;
        var recorder = PreTesterRecorder ?? PreTesterEvidenceRecorder.FromLegacy(_recordFindingEvidenceRequest, _recordFindingEvidenceRun);
        var (prior, needsReplacementStartedEntry) = PreTesterStoredEntryRecovery.ReadAndHeal(goal, tester.Id, candidateSha, (entry, line) => recorder.Run(goal.Id, tester.Id, entry, line));
        var decline = EvaluatePreTesterDeferredEvidenceGuard(developer, candidateSha, out var testsField);
        if (decline != PreTesterDeferredEvidenceDeclineClause.None)
        {
            RecordPreTesterDeferredEvidenceDecline(goal, developer, decline, candidateSha);
            PreTesterStartedEvidence.TryAbandon(prior, decline.ToString(), (entry, line) => recorder.Run(goal.Id, tester.Id, entry, line));
            return false;
        }
        var priorEvidence = prior is { Outcome: "unusable" } ? _focusedEvidenceAttemptCoordinator.ReadFocusedReceiptEvidence(goal.Id, prior.ReceiptId, candidateSha!) : null;
        var apparatusUnusable = PreTesterApparatusReceiptClassifier.IsApparatusUnusable(priorEvidence);
        var refreshCount = PreTesterEvidenceRefreshRule.CountForCandidate(goal, tester.Id, candidateSha!);
        var superseded = PreTesterEvidenceRefreshRule.Decide(prior, apparatusUnusable, tester.PendingRetryCause, refreshCount) == PreTesterEvidenceRefreshDecision.Refresh ||
            PreTesterEvidenceRefreshRule.DecideBuildPhaseRefresh(prior, apparatusUnusable, PreTesterApparatusReceiptClassifier.IsBuildPhaseOnly(priorEvidence), refreshCount) == PreTesterEvidenceRefreshDecision.Refresh ? prior!.ReceiptId : null;
        if (superseded is not null) prior = null;
        if (prior is not null && prior.Outcome != "started")
        {
            if (prior.Outcome == "actionable-red")
            {
                if (TryEscalatePreTesterRedLoop(goal, goalPrefix, policy, fromState, out result)) return true;
                throw new InvalidDataException("Actionable pre-Tester RED has no Developer retry or escalation.");
            }
            if (prior.Outcome == "red" && OutOfDiffRerun(worktreePath).TryRun(goal, tester, prior, policy, out var priorHold, out var priorHoldKind))
            {
                result = MakeResult(goal.Id.Value, goalPrefix, policy, FocusedEvidencePendingHeld(fromState, priorHold, priorHoldKind));
                return true;
            }
            RecordPreTesterDeferredEvidenceExit(goal, developer, testsField,
                PreTesterDeferredEvidenceExit.PriorOutcomeNotStarted, prior.Outcome,
                DeveloperDeferredTestClassNames.Parse(testsField));
            return false;
        }
        var observation = PreTesterAttemptObservation.ReadRunning(
            goal, prior, candidateSha!, policy, _focusedEvidenceAttemptCoordinator, TryReconcileFocusedEvidenceAttempt);
        if (observation is { Completed: false, Kind: ConductorParallelAcceptanceAttemptDecisionKind.Running })
        {
            result = MakeResult(goal.Id.Value, goalPrefix, policy, FocusedEvidencePendingHeld(fromState, observation.Decision, observation.Kind));
            return true;
        }
        var declaration = ConductorTickStepLedger.Measure("walk-pretester-declared-resolve",
            () => DeveloperDeferredTestSelections.Resolve(worktreePath, testsField, _cohortWorkspace?.ProjectHomeDirectoryOrNull));
        if (declaration.Selections.Count == 0)
        {
            if (PreTesterStartedEvidence.TryAbandon(prior, nameof(PreTesterDeferredEvidenceExit.NoResolvedClasses), (entry, line) => recorder.Run(goal.Id, tester.Id, entry, line)))
                return false;
            RecordPreTesterDeferredEvidenceExit(goal, developer, testsField,
                PreTesterDeferredEvidenceExit.NoResolvedClasses, prior?.Outcome, declaration.NotRun);
            return false;
}
        var settings = _getFindingEvidenceEngineSettings(goal);
        var normalizedDeclaration = DeveloperDeferredTestSelectionNormalization.Normalize(declaration, settings);
        var selected = new List<FindingEvidenceSelection>(normalizedDeclaration.Selections);
        var notRun = new List<string>(normalizedDeclaration.NotRun);
        if (selected.Count == 0)
        {
            if (PreTesterStartedEvidence.TryAbandon(prior, nameof(PreTesterDeferredEvidenceExit.NoNormalizedClasses), (entry, line) => recorder.Run(goal.Id, tester.Id, entry, line)))
                return false;
            RecordPreTesterDeferredEvidenceExit(goal, developer, testsField,
                PreTesterDeferredEvidenceExit.NoNormalizedClasses, prior?.Outcome, notRun);
            return false;
        }

        foreach (var selection in PreTesterNamedTestClasses.Select(goal, tester, worktreePath))
        {
            if (TryNormalizeFindingEvidenceRequest(
                    new FindingEvidenceRequest([selection]), settings,
                    (_, _) => [],
                    out var normalized, out _, out _, out _))
                selected.AddRange(normalized.Selections);
        }

        // Open requests follow Developer declarations, then brief- and Planner-named classes.
        foreach (var finding in goal.Tasks
                     .Where(task => task.RequiredRole is AgentRole.Tester or AgentRole.Reviewer)
                     .SelectMany(task => task.LastVerification?.MergedReviewFindings ?? [])
                     .Where(finding => finding.State == ReviewFindingState.Open && finding.EvidenceRequest is not null))
        {
            foreach (var selection in finding.EvidenceRequest!.Selections)
            {
                if (TryNormalizeFindingEvidenceRequest(
                        new FindingEvidenceRequest([selection]), settings,
                        (project, name) => _resolveFindingEvidenceSiblingClasses(goal, project, name),
                        out var normalized, out _, out _, out _))
                    selected.AddRange(normalized.Selections);
            }
        }
        var (guards, augmented, abandoned) = PreTesterGuardSelections.CreateForRequest(_preTesterChangedFileGuards, worktreePath,
            _getLandingFileScopes(goal), PreTesterMainRevision?.Invoke(worktreePath) ??
            PreTesterGuardSelections.ReadMainRevision(worktreePath, _cohortWorkspace), new(selected, notRun), settings, prior,
            FormatFindingEvidenceSelection, (entry, line) => recorder.Run(goal.Id, tester.Id, entry, line));
        if (abandoned) prior = null;
        needsReplacementStartedEntry |= abandoned;
        var distinct = augmented.Selections.ToArray();
        notRun = augmented.NotRun.ToList();
        var request = string.Join("; ", distinct.Select(FormatFindingEvidenceSelection));
        var selectionNames = distinct.Select(FormatFindingEvidenceSelection).ToArray();
        var identity = FindingEvidenceExecutionClassifier.BuildRequestIdentity(new FindingEvidenceRequest(distinct));
        var refresh = PreTesterEvidenceRefreshRule.LatestForCandidate(goal, tester.Id, candidateSha!);
        var refreshId = CreateFindingEvidenceReceiptId(candidateSha!, "pre-tester-deferred-refresh", identity);
        var salt = superseded is not null || prior?.ReceiptId == refresh?.FreshReceiptId && prior?.ReceiptId == refreshId
            ? "pre-tester-deferred-refresh" : "pre-tester-deferred";
        var receiptId = CreateFindingEvidenceReceiptId(candidateSha!, salt, identity);
        if (superseded is not null)
        {
            refresh = new(candidateSha!, superseded, receiptId);
            _recordFindingEvidenceRequest(goal.Id, tester.Id, PreTesterEvidenceRefreshRule.FormatMarker(refresh));
        }
        var requestContext = new ConductorFocusedEvidenceRequestContext(
            "pre-tester-deferred",
            CreateFindingEvidenceBatchId(candidateSha!, salt, policy.Name, identity),
            [], ExecutionBasisIdentity: BuildFindingEvidenceExecutionBasisIdentity(settings), ReceiptId: receiptId);
        if (prior is null && needsReplacementStartedEntry)
            PreTesterStartedEvidence.RecordStarted(candidateSha!, receiptId, selectionNames, notRun, (entry, line) => recorder.Request(goal.Id, tester.Id, entry, line));
        observation ??= PreTesterAttemptObservation.Reconcile(
            goal, policy, request, candidateSha, requestContext, TryReconcileFocusedEvidenceAttempt);
        var (completed, evidence, attempt, decision, kind) = observation;
        if (observation.IsPending)
        {
            if (prior is null && !needsReplacementStartedEntry && kind == ConductorParallelAcceptanceAttemptDecisionKind.Started)
                PreTesterStartedEvidence.RecordStarted(candidateSha!, receiptId, selectionNames, notRun, (entry, line) => recorder.Request(goal.Id, tester.Id, entry, line));
            result = MakeResult(goal.Id.Value, goalPrefix, policy,
                FocusedEvidencePendingHeld(fromState, decision, kind));
            return true;
        }
        var liveHead = _getPreReviewEvidenceContext(goal).CandidateSha?.Trim();
        if (PreTesterDeferredEvidenceReads.IsSupersededPreTesterCandidate(worktreePath, candidateSha, liveHead))
        {
            decision = FailedGoalFindingObservation.Observed(
                FailedGoalFindingObservationKind.FindingEvidencePending,
                $"Pre-Tester focused evidence for candidate {FormatShortSha(candidateSha)} " +
                $"superseded by live head {FormatShortSha(liveHead)}; re-running at the live head.");
            result = MakeResult(goal.Id.Value, goalPrefix, policy,
                FocusedEvidencePendingHeld(fromState, decision, kind));
            return true;
        }

        var attributionBatch = new FindingEvidenceBatch(
            identity, request, new FindingEvidenceRequest(distinct), [], []);
        var (outcome, candidate, failingTests, unusable) = PreTesterApparatusReceiptClassifier.ClassifyOutcome(evidence, completed, candidateSha, distinct,
            failures => guards.EveryFailureIsInsideChanges(failures,
                otherFailures => EveryFailingTestIsInsideCandidateChanges(goal, attributionBatch, otherFailures)));
        if (attempt is not null)
            _focusedEvidenceAttemptCoordinator.RecordFocusedEvidenceRequestDispositions(attempt, requestContext.FindingRoundFingerprint, receiptId, []);
        var runEntry = new PreTesterEvidenceEntry(outcome, candidateSha!, receiptId, selectionNames, notRun, attempt?.ResultPath, failingTests);
        recorder.Run(goal.Id, tester.Id, runEntry, PreTesterUnusableReason.AppendTo(unusable, PreTesterFailureSignatureReader.AppendTo(
            PreTesterEvidenceIndexLines.FormatMarker(runEntry), outcome, failingTests, candidate?.TestResultPaths)));
        if (PreTesterEvidenceRefreshRule.DecideAfterRun(refresh, receiptId, outcome) == PreTesterEvidenceRefreshDecision.ContinueAfterUnusableRefresh)
        {
            RecordPreTesterDeferredEvidenceContinuation(goal, developer, candidateSha!, refresh!.SupersededReceiptId, receiptId);
            return false;
        }
        if (outcome == "unusable" && PreTesterApparatusReceiptClassifier.IsBuildPhaseOnly(evidence) &&
            PreTesterEvidenceRefreshRule.CountForCandidate(goal, tester.Id, candidateSha!) == 0)
        {
            result = MakeResult(goal.Id.Value, goalPrefix, policy, new ConductorAdvanceOutcome.Held(fromState,
                $"PRE_TESTER_BUILD_PHASE_REFRESH: candidate_sha={candidateSha}; receipt_id={receiptId}"));
            return true;
        }
        if (outcome == "red" && OutOfDiffRerun(worktreePath).TryRun(GetCurrentGoal(goal), tester, runEntry, policy, out var hold, out var holdKind))
        {
            result = MakeResult(goal.Id.Value, goalPrefix, policy, FocusedEvidencePendingHeld(fromState, hold, holdKind));
            return true;
        }
        if (outcome != "actionable-red") return false;
        if (TryEscalatePreTesterRedLoop(GetCurrentGoal(goal), goalPrefix, policy, fromState, out result))
            return true;

        var feedback = AppendActionableCandidateRedFailureDetail(
            FormatActionableCandidateRedMessage(candidateSha!, receiptId, "pre-tester-deferred", failingTests),
            receiptId, failingTests, candidate?.TestResultPaths);
        RetryDeveloperWithAcceptedFeedback(
            goal.Id, developer.Id, feedback, RetryRoundKind.Mechanical, RetryCause.NewSourceFinding);
        var refreshed = GetCurrentGoal(goal);
        result = ExecuteDispatchAndStart(
            refreshed, goalPrefix, policy, GoalLifecycle.ResolveState(refreshed, GetFacts(refreshed)));
        return true;
    }

    private bool TryEscalatePreTesterRedLoop(
        Goal goal,
        string goalPrefix,
        ConductorAutonomyPolicy policy,
        GoalLifecycleState fromState,
        out ConductorAdvanceResult result)
    {
        result = default!;
        var tester = goal.Tasks.First(task => task.RequiredRole == AgentRole.Tester);
        var lastOperatorAnswer = PreTesterDeferredEvidenceReads.LastOperatorRedLoopAnswerIndex(goal);
        var redHistory = goal.Timeline
            .Where((evt, index) => index > lastOperatorAnswer)
            .Where(evt => evt.TaskId == tester.Id && evt.Kind == ProgressKind.FindingEvidenceRunRecorded &&
                          PreTesterEvidenceEntryReader.Read(evt)?.Outcome == "actionable-red")
            .ToArray();
        var lastTesterDispatch = goal.Timeline
            .Where(evt => evt.TaskId == tester.Id && evt.Kind == ProgressKind.TaskDispatchRecorded)
            .Select(evt => evt.OccurredAt).DefaultIfEmpty(DateTimeOffset.MinValue).Max();
        var window = redHistory.Where(evt => evt.OccurredAt > lastTesterDispatch).ToArray();
        var decision = PreTesterRedLoopRule.EvaluateRuns(
            window.Select(evt => PreTesterFailureSignature.ReadRun(evt)).ToArray());
        if (!decision.Trip) return false;
        result = Escalate(goal, goalPrefix, policy, fromState,
            "PRE_TESTER_RED_LOOP: three consecutive candidate RED runs without Tester dispatch; " +
            $"failing_sets={string.Join(" | ", window.Select(evt => evt.Message))}; " +
            $"failing_counts={string.Join(',', decision.FailingCounts)}; trip={decision.Kind}");
        return true;
    }

}
