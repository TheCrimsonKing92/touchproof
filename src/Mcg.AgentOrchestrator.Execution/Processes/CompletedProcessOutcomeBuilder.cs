using System.Text.Json;
using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.Core;
using static Mcg.AgentOrchestrator.Infrastructure.BackgroundDispatchRunner;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed class CompletedProcessOutcomeBuilder
{
    private readonly BackgroundDispatchRunner _runner;
    private readonly IClock _clock;
    private readonly ProcessLogReader _processLogReader;
    private readonly WorkerDispatchCompletionClassifier _completionClassifier;
    private readonly DispatchWorktreeCommitter _worktreeCommitter;
    private readonly DispatchProcessRecoveryService _recoveryService;
    private readonly InterruptedWorkCheckpointAuthorizer _checkpointAuthorizer;
    private readonly Func<OrchestratorBuildCheckRequest, OrchestratorBuildCheckResult> _runOrchestratorBuildCheck;
    private readonly ICompletionBuildEvidenceOperation? _completionBuildEvidence;
    private readonly Func<string, IReadOnlyList<ProcessInspectionRecord>> _findBuildDaemons;
    private readonly Func<ProcessInspectionRecord, bool> _tryKillBuildDaemon;

    internal CompletedProcessOutcomeBuilder(
        BackgroundDispatchRunner runner,
        IClock clock,
        ProcessLogReader processLogReader,
        WorkerDispatchCompletionClassifier completionClassifier,
        DispatchWorktreeCommitter worktreeCommitter,
        DispatchProcessRecoveryService recoveryService,
        InterruptedWorkCheckpointAuthorizer checkpointAuthorizer,
        Func<OrchestratorBuildCheckRequest, OrchestratorBuildCheckResult> runOrchestratorBuildCheck,
        ICompletionBuildEvidenceOperation? completionBuildEvidence,
        Func<string, IReadOnlyList<ProcessInspectionRecord>> findBuildDaemons,
        Func<ProcessInspectionRecord, bool> tryKillBuildDaemon)
    {
        _runner = runner;
        _clock = clock;
        _processLogReader = processLogReader;
        _completionClassifier = completionClassifier;
        _worktreeCommitter = worktreeCommitter;
        _recoveryService = recoveryService;
        _checkpointAuthorizer = checkpointAuthorizer;
        _runOrchestratorBuildCheck = runOrchestratorBuildCheck;
        _completionBuildEvidence = completionBuildEvidence;
        _findBuildDaemons = findBuildDaemons;
        _tryKillBuildDaemon = tryKillBuildDaemon;
    }

    internal DispatchRefreshOutcome Build(
        AgentOrchestratorKernel kernel,
        GoalId goalId,
        TaskId taskId,
        TaskProcessRecord processRecord,
        int exitCode,
        string? standardErrorDiagnostic = null,
        DispatchRecoveryDecision? recoveryDecision = null,
        TaskProcessResourceAccounting? capturedResourceAccounting = null,
        string? orchestratorFailureReason = null)
    {
        var observedExitCode = exitCode;
        var task = kernel.GetTask(goalId, taskId);
        if (TryReadIntentSuppression(goalId, taskId, task, processRecord, exitCode,
                out var suppressedPrimaryWithStartedSamples) is { } suppression)
            return new DispatchRefreshOutcome(processRecord with { CompletedAt = _clock.UtcNow, ExitCode = exitCode },
                null, IntentSuppression: suppression);
        var exitArtifactAlreadyExisted = File.Exists(processRecord.ExitCodePath);
        var dispatchAttempt = ResolveDispatchAttempt(task, processRecord);
        if (task.RequiredRole is AgentRole.Reviewer or AgentRole.Tester &&
            SubscriptionLimitEvidence.IsSubscriptionProviderCliDispatch(task) &&
            dispatchAttempt?.DispatchedAt != task.LastDispatch!.DispatchedAt)
            return new DispatchRefreshOutcome(processRecord, null);
        DateTimeOffset? contextReceiptAttemptAt = dispatchAttempt?.ContextPackageReceipt is null
            ? null
            : dispatchAttempt.DispatchedAt;
        var providerUsage = _runner.ResolveDispatchProviderUsage(dispatchAttempt, processRecord.StandardOutputPath);
        if (task.RequiredRole == AgentRole.Tester && SubscriptionLimitEvidence.IsSubscriptionProviderCliDispatch(task))
        {
            WorkerResultSubmissionIngestion.TryIngest(processRecord.WorkingDirectory, goalId, task,
                dispatchAttempt!.DispatchedAt, processRecord.StandardOutputPath, out var submissionDiagnostic);
            if (!string.IsNullOrEmpty(submissionDiagnostic))
                standardErrorDiagnostic = AppendDiagnostic(standardErrorDiagnostic ?? string.Empty, submissionDiagnostic);
        }
        var outputSnapshot = _processLogReader.ReadBestEffort(processRecord, processRecord.StandardOutputPath);
        var errorSnapshot = _processLogReader.ReadBestEffort(processRecord, processRecord.StandardErrorPath);
        var decisionStandardOutput = outputSnapshot.DecisionText;
        var decisionStandardError = errorSnapshot.DecisionText;
        var fullStandardOutput = _processLogReader.ReadComplete(processRecord.StandardOutputPath);
        var authoritativeStandardOutput = fullStandardOutput.Content is { } initialAuthoritativeContent
            ? ProcessLogReader.NormalizeCompleteDecisionText(initialAuthoritativeContent)
            : decisionStandardOutput;
        if (outputSnapshot.DecisionTruncatedChars > 0)
        {
            standardErrorDiagnostic = AppendDiagnostic(
                standardErrorDiagnostic ?? string.Empty,
                $"decision-text-truncated source=stdout dropped_chars={outputSnapshot.DecisionTruncatedChars} " +
                $"dropped_scope=selected-decision-content retained_chars={outputSnapshot.DecisionText.Length} " +
                $"cap={VerificationTextBounds.MaxRetainedChars} " +
                $"complete_artifact='{processRecord.StandardOutputPath}'");
        }
        if (errorSnapshot.DecisionTruncatedChars > 0)
        {
            standardErrorDiagnostic = AppendDiagnostic(
                standardErrorDiagnostic ?? string.Empty,
                $"decision-text-truncated source=stderr dropped_chars={errorSnapshot.DecisionTruncatedChars} " +
                $"dropped_scope=selected-decision-content retained_chars={errorSnapshot.DecisionText.Length} " +
                $"cap={VerificationTextBounds.MaxRetainedChars} " +
                $"complete_artifact='{processRecord.StandardErrorPath}'");
        }
        string? finalPlannerRejectionDiagnostic = null;
        var hasChildExitRecord = TryReadChildExitRecord(processRecord.ChildExitRecordPath, out var childExitRecord);
        var wrapperExitReconciled = false;
        var resourceAccounting = capturedResourceAccounting;
        var goal = kernel.GetGoal(goalId);
        var humanInputDirective = PlannerOutputContract.ParseEvidenceRequests(authoritativeStandardOutput, task.RequiredRole, fullStandardOutput.Content is not null);
        var hasRoleCapability = DispatchRoleOutputCapabilities.TryGet(task.RequiredRole, out var dispatchRoleCapability);
        var requiresTerminalContract = task.RequiredRole is AgentRole.Reviewer or AgentRole.Tester &&
            SubscriptionLimitEvidence.IsSubscriptionProviderCliDispatch(task);
        var terminalContract = requiresTerminalContract
            ? WorkerResultBlockers.AssessTerminalVerificationOutput(task.RequiredRole,
                fullStandardOutput.Content is null ? null : authoritativeStandardOutput)
            : new VerificationTerminalContractAssessment(VerificationTerminalContractStatus.NotApplicable, string.Empty);
        var providerCancelled = requiresTerminalContract && ProviderFailureSignals.IsProviderInterruptionFailure(
            new TaskVerificationRecord(processRecord.Command, processRecord.WorkingDirectory, observedExitCode,
                authoritativeStandardOutput, decisionStandardError, _clock.UtcNow,
                StandardOutputPath: processRecord.StandardOutputPath, EvidenceSource: VerificationEvidenceSource.Dispatch,
                DispatchAttemptAt: dispatchAttempt?.DispatchedAt));
        var completeNonBlockedWorkerResult = hasRoleCapability && _completionClassifier.HasSuccessfulWorkerResult(
            processRecord.WorkingDirectory,
            authoritativeStandardOutput,
            decisionStandardError,
            allowNoChangedFiles: true,
            requireNoBlockers: true,
            roleCapability: dispatchRoleCapability);
        completeNonBlockedWorkerResult &= terminalContract.AllowsCompletion && !providerCancelled;
        var successfulChildResultAvailable =
            observedExitCode != 0 &&
            hasChildExitRecord &&
            childExitRecord.ExitCode == 0 &&
            completeNonBlockedWorkerResult;
        var successfulChildWithoutUsableWorkerResult =
            observedExitCode != 0 &&
            hasChildExitRecord &&
            childExitRecord.ExitCode == 0 &&
            !completeNonBlockedWorkerResult;
        var completionContractSucceeded = terminalContract.AllowsCompletion && !providerCancelled;

        if (task.RequiredRole == AgentRole.Researcher &&
            RequiresDurableResearchArtifact(goal, task) &&
            (exitCode == 0 || successfulChildResultAvailable))
        {
            var capturedResearchOutput = ResearcherOutputContract.ReadCapturedOutputTail(processRecord.StandardOutputPath);
            var researchContract = ResearcherOutputContract.Resolve(capturedResearchOutput);
            if (!researchContract.Succeeded || researchContract.Research is null)
            {
                exitCode = 1;
                completionContractSucceeded = false;
                standardErrorDiagnostic = AppendDiagnostic(
                    AppendDiagnostic(
                        standardErrorDiagnostic ?? string.Empty,
                        researchContract.Diagnostic),
                    DispatchFailureDiagnosticMarker.Format(DispatchFailureDiagnosticMarker.ResearcherOutputContractRejected));
            }
            else if (!ResearcherOutputContract.TryPersistDurableReceipt(
                         processRecord.StandardOutputPath,
                         researchContract.Research,
                         out var appendDiagnostic))
            {
                exitCode = 1;
                completionContractSucceeded = false;
                standardErrorDiagnostic = AppendDiagnostic(
                    AppendDiagnostic(
                        standardErrorDiagnostic ?? string.Empty,
                        $"Researcher output contract could not persist the accepted artifact: {appendDiagnostic}. Retry Researcher for contract repair."),
                    DispatchFailureDiagnosticMarker.Format(DispatchFailureDiagnosticMarker.ResearcherArtifactPersistenceFailed));
            }
        }

        PlannerCandidateDivergenceReceipt? plannerCandidateDivergence = null;
        var scoutResearchSourcePath = processRecord.StandardOutputPath;
        if (task.RequiredRole == AgentRole.Planner &&
            (exitCode == 0 || successfulChildResultAvailable || suppressedPrimaryWithStartedSamples))
        {
            var acceptanceCriteria = RequiresDurablePlanArtifact(goal, task)
                ? goal.RefinedSpec?.AcceptanceCriteria ?? []
                : null;
            var capturedPlannerOutput = PlannerOutputContract.ReadCapturedOutputTail(processRecord.StandardOutputPath);
            var evidenceRequest = humanInputDirective;
            PlannerOutputContractResult plannerContract;
            if (evidenceRequest.IsMalformed)
            {
                plannerContract = new PlannerOutputContractResult(false, null, null, evidenceRequest.Diagnostic!);
            }
            else if (task.LastDispatch?.PlannerSampleCount > 1)
            {
                var candidates = PlannerSampleDispatcher.CollectCandidates(
                        processRecord.StandardOutputPath,
                        task.LastDispatch.PlannerSampleCount,
                        dispatchAttempt,
                        processRecord.CompletedAt is { } completedAt
                            ? Math.Max(0, (long)(completedAt - processRecord.StartedAt).TotalMilliseconds)
                            : null);
                if (suppressedPrimaryWithStartedSamples)
                    candidates = candidates.Select(candidate => candidate.Index == 0
                        ? candidate with { TerminalState = PlannerCandidateTerminalState.IntentSuppressed }
                        : candidate).ToArray();
                var selection = PlannerCandidateSelector.Select(
                    candidates,
                    processRecord.WorkingDirectory,
                    acceptanceCriteria);
                plannerContract = selection.SelectedContract;
                plannerCandidateDivergence = selection.Receipt;
                if (suppressedPrimaryWithStartedSamples && plannerContract.Succeeded)
                    exitCode = 0;
                if (selection.Receipt.SelectedCandidateIndex is int selectedIndex)
                    scoutResearchSourcePath = candidates[selectedIndex].SourcePath
                        ?? throw new InvalidOperationException("Selected Planner candidate has no stdout source path.");
            }
            else
            {
                plannerContract = PlannerOutputContract.Resolve(
                    capturedPlannerOutput,
                    decisionStandardError,
                    processRecord.WorkingDirectory,
                    acceptanceCriteria: acceptanceCriteria);
            }
            if (!plannerContract.Succeeded || plannerContract.Plan is null)
            {
                exitCode = 1;
                completionContractSucceeded = false;
                var rejectionDiagnostic = plannerContract.Diagnostic;
                if (!PlannerOutputContract.TryPersistRejectionDiagnostic(
                        processRecord.StandardErrorPath,
                        rejectionDiagnostic,
                        out var persistenceDiagnostic))
                {
                    rejectionDiagnostic = AppendDiagnostic(
                        rejectionDiagnostic,
                        $"Full Planner rejection could not be persisted to '{processRecord.StandardErrorPath}': {persistenceDiagnostic}");
                }

                finalPlannerRejectionDiagnostic = rejectionDiagnostic;
                standardErrorDiagnostic = AppendDiagnostic(
                    standardErrorDiagnostic ?? string.Empty,
                    DispatchFailureDiagnosticMarker.Format(DispatchFailureDiagnosticMarker.PlannerOutputContractRejected));
            }
            else
            {
                var durableSource = plannerContract.IngestedPath ?? processRecord.StandardOutputPath;
                if (!PlannerOutputContract.TryPersistDurableReceipt(
                        processRecord.StandardOutputPath,
                        durableSource,
                        plannerContract.Plan,
                        out var appendDiagnostic))
                {
                    exitCode = 1;
                    completionContractSucceeded = false;
                    standardErrorDiagnostic = AppendDiagnostic(
                        AppendDiagnostic(
                            standardErrorDiagnostic ?? string.Empty,
                            $"Planner output contract could not persist the accepted plan: {appendDiagnostic}. Retry Planner for contract repair."),
                        DispatchFailureDiagnosticMarker.Format(DispatchFailureDiagnosticMarker.PlannerPlanPersistenceFailed));
                }
                else if (RequiresDurableScoutResearchArtifact(goal, task) &&
                    !TryPersistScoutResearch(scoutResearchSourcePath, processRecord.StandardOutputPath, out var scoutDiagnostic))
                {
                    exitCode = 1;
                    completionContractSucceeded = false;
                    standardErrorDiagnostic = AppendDiagnostic(
                        standardErrorDiagnostic ?? string.Empty,
                        scoutDiagnostic);
                }
            }
        }

        // Refresh after role-specific contracts may have appended durable receipts, then reuse this same
        // complete artifact for presence and the stored verification record. Normalize only bare carriage
        // returns for parsing; the authoritative stored content remains byte-complete and unchanged.
        fullStandardOutput = _processLogReader.ReadComplete(processRecord.StandardOutputPath);
        authoritativeStandardOutput = fullStandardOutput.Content is { } authoritativeContent
            ? ProcessLogReader.NormalizeCompleteDecisionText(authoritativeContent)
            : authoritativeStandardOutput;
        var providerFailureKind = _completionClassifier.ParseProviderFailureKind(task.LastDispatch, observedExitCode, decisionStandardOutput, decisionStandardError);
        var workerResultPresent = _completionClassifier.HasWorkerResultArtifact(
            processRecord.WorkingDirectory,
            authoritativeStandardOutput);
        if (requiresTerminalContract)
            workerResultPresent = terminalContract.Status == VerificationTerminalContractStatus.Valid && !providerCancelled;
        var hasCommittedChanges = false;
        var orchestratorCommitted = false;
        var buildEvidenceAllowsResultCommit = true;
        InterruptedWorkCheckpointDisposition? checkpointDisposition = null;
        FailedRoundCheckpointReceipt? failedRoundReceipt = null;
        var completedWorktreeInspection = RequiresFileChangeEvidence(task)
            ? _worktreeCommitter.InspectGoalWorktree(
                processRecord.WorkingDirectory,
                goalId,
                task.LastDispatch!.DispatchedAt,
                forceRefresh: true)
            : null;
        var worktreeEvidenceAvailable = completedWorktreeInspection is { IsAvailable: true };
        var initialWorktreeEvidence = worktreeEvidenceAvailable
            ? completedWorktreeInspection!.Evidence
            : GoalWorktreeDispatchEvidence.Unknown;
        hasCommittedChanges = initialWorktreeEvidence.HasRelevantCommitAfterDispatch;
        var hasQualifyingDirtyChanges =
            worktreeEvidenceAvailable &&
            !initialWorktreeEvidence.IsClean &&
            initialWorktreeEvidence.DirtyPaths.Count > 0;
        var relevantChangeEvidenceAvailable =
            worktreeEvidenceAvailable && (hasCommittedChanges || hasQualifyingDirtyChanges);
        var reconciliationOriginRule = successfulChildResultAvailable
            ? _completionClassifier.ClassifyReconciliationOriginRule(
                task,
                processRecord,
                observedExitCode,
                decisionStandardOutput,
                decisionStandardError,
                standardErrorDiagnostic,
                workerResultPresent,
                hasCommittedChanges,
                providerFailureKind,
                childExitRecord,
                recoveryDecision)
            : null;
        var reconcileWrapperExit = ShouldReconcileWrapperExit(new WrapperExitReconciliationEvidence(
            observedExitCode,
            hasChildExitRecord ? childExitRecord.ExitCode : null,
            completeNonBlockedWorkerResult,
            completionContractSucceeded,
            hasRoleCapability,
            dispatchRoleCapability,
            relevantChangeEvidenceAvailable,
            humanInputDirective.Directives.Count > 0 || humanInputDirective.IsMalformed,
            !string.IsNullOrWhiteSpace(orchestratorFailureReason)));
        if (completedWorktreeInspection is { IsAvailable: true, Evidence: var worktreeEvidence })
        {
            // Default path: a Developer/Tester that edited the worktree and showed verification
            // evidence does not need to self-commit. The orchestrator stages and commits the dirty
            // diff after guards pass. Dirty-but-unverified edits are left dirty and fail.
            var originalExitCode = exitCode;
            var commitAttempted = false;
            var commitAttempt = default(CommitWorktreeEditsResult);
            var sandboxCommitBlocked = _completionClassifier.HasSandboxCommitBlockedEvidence(
                task.RequiredRole,
                decisionStandardOutput,
                decisionStandardError);
            var sandboxCommitOnBehalfEvidence =
                sandboxCommitBlocked || providerFailureKind == ProviderFailureKind.Sandbox1312;
            var lowIntegrityConfinementEvidence = _completionClassifier.HasLowIntegrityConfinementEvidence(
                task.LastDispatch,
                processRecord,
                decisionStandardError,
                sandboxCommitOnBehalfEvidence);
            // Blockers remain advisory on the ordinary exit-0 commit path. The stricter
            // completeNonBlockedWorkerResult is reserved for overriding a failed wrapper exit.
            var successfulWorkerResult = terminalContract.AllowsCompletion && !providerCancelled && _completionClassifier.HasSuccessfulWorkerResult(
                processRecord.WorkingDirectory,
                decisionStandardOutput,
                decisionStandardError);
            var failedBuildCheckDiagnostic = string.Empty;
            var failedWorkerBuildCheck = task.LastDispatch?.WorkerBuildProtocolVersion != 1 && _completionClassifier.TryFindFailedWorkerBuildCheck(
                    processRecord.WorkingDirectory,
                    decisionStandardOutput,
                    decisionStandardError,
                    out failedBuildCheckDiagnostic);
            if (failedWorkerBuildCheck)
            {
                exitCode = 1;
                standardErrorDiagnostic = AppendDiagnostic(
                    AppendDiagnostic(
                        standardErrorDiagnostic ?? string.Empty,
                        failedBuildCheckDiagnostic),
                    DispatchFailureDiagnosticMarker.Format(DispatchFailureDiagnosticMarker.WorkerBuildCheckFailed));
            }

            Func<OrchestratorBuildEvidenceResolution> reconcileBuild = () => CompletionBuildDispatchReconciliationPolicy.Resolve(
                task, processRecord.WorkingDirectory,
                worktreeEvidence.ChangedPaths, worktreeEvidence.DirtyPaths, failedWorkerBuildCheck,
                () => _completionClassifier.EvaluateWorkerBuildReceipt(processRecord.WorkingDirectory, goal.Id, task),
                _runOrchestratorBuildCheck,
                _completionBuildEvidence is null ? null : request => _completionBuildEvidence.Reconcile(
                    kernel, goalId, taskId, request, processRecord with
                    { CompletedAt = processRecord.CompletedAt ?? _clock.UtcNow, ExitCode = observedExitCode }), goal.Id);
            var buildEvidence = task.LastDispatch?.WorkerBuildProtocolVersion == 1
                ? ResolveWorkerBuildInvocation(kernel, goalId, taskId, worktreeEvidence.ChangedPaths, worktreeEvidence.DirtyPaths, decisionStandardOutput + Environment.NewLine + decisionStandardError, completeNonBlockedWorkerResult, reconcileBuild, ref orchestratorFailureReason) : reconcileBuild();
            if (buildEvidence.Pending)
                return new DispatchRefreshOutcome(
                    processRecord with { CompletedAt = processRecord.CompletedAt ?? _clock.UtcNow, ExitCode = observedExitCode }, null,
                    PendingBuildEvidence: buildEvidence.Operation);

            buildEvidenceAllowsResultCommit = !failedWorkerBuildCheck && !buildEvidence.FailsRound;
            checkpointDisposition = buildEvidence.FailsRound
                ? new InterruptedWorkCheckpointDisposition(InterruptedWorkCheckpointDispositionKind.NotApplicable,
                    "completion-build-evidence-not-certified", string.Empty)
                : _checkpointAuthorizer.AuthorizeAndCheckpoint(
                new InterruptedWorkCheckpointRequest(
                    goalId,
                    task,
                    processRecord,
                    recoveryDecision,
                    providerFailureKind,
                    completedWorktreeInspection,
                    BuildDispatchId(goalId, taskId, task.LastDispatch!),
                    failedWorkerBuildCheck));
            if (checkpointDisposition.Kind is InterruptedWorkCheckpointDispositionKind.Hold or
                InterruptedWorkCheckpointDispositionKind.CommitFailed)
            {
                standardErrorDiagnostic = AppendDiagnostic(
                    standardErrorDiagnostic ?? string.Empty,
                    checkpointDisposition.Message);
            }

            var missingWorkerBuildEvidence = buildEvidence.MissingEvidence;
            standardErrorDiagnostic = buildEvidence.AppendDiagnostic(standardErrorDiagnostic);
            if (buildEvidence.FailsRound)
            {
                exitCode = 1;
            }
            var provider = _runner.ResolveWorkerProvider(task.LastDispatch);
            var normalIntegrityCommitEvidence =
                task.LastDispatch.SandboxLowIntegrity != true &&
                (successfulWorkerResult || worktreeEvidence.HasRelevantCommitAfterDispatch);
            var shouldCommitDirtyWorktree =
                !buildEvidence.FailsRound &&
                !missingWorkerBuildEvidence &&
                checkpointDisposition?.IsCheckpoint != true &&
                (recoveryDecision?.Action != DispatchRecoveryAction.PreserveInterruptedWork || reconcileWrapperExit) &&
                ((exitCode == 0 && (normalIntegrityCommitEvidence || lowIntegrityConfinementEvidence)) ||
                 reconcileWrapperExit ||
                 (task.LastDispatch.SandboxLowIntegrity && sandboxCommitOnBehalfEvidence) ||
                 (originalExitCode != 0 && successfulWorkerResult && !provider.Capabilities.CanSelfCommit && lowIntegrityConfinementEvidence));

            if (buildEvidence.Operation is { Phase: CompletionBuildEvidencePhase.Success } built)
            {
                var currentReceipt = _completionClassifier.EvaluateWorkerBuildReceipt(processRecord.WorkingDirectory, goal.Id, task);
                var treeChanged = !WorktreeTreeDigest.TryCompute(processRecord.WorkingDirectory, out var currentDigest, out _) || currentDigest != built.TreeDigest || !currentReceipt.Matches;
                IReadOnlyList<string> uncovered = currentReceipt.Matches ? WorkerBuildReceiptCoverage.FindMissingProjects(built.Projects, currentReceipt.Projects) : [];
                if (treeChanged || uncovered.Count != 0)
                {
                    shouldCommitDirtyWorktree = false;
                    missingWorkerBuildEvidence = true;
                    buildEvidenceAllowsResultCommit = false;
                    exitCode = 1;
                    kernel.RecordCompletionBuildEvidence(goalId, taskId, built with
                    { Phase = CompletionBuildEvidencePhase.StaleCandidate, Diagnostic = "Build candidate evidence changed before commit authorization." });
                    standardErrorDiagnostic = AppendDiagnostic(standardErrorDiagnostic ?? string.Empty,
                        DispatchFailureDiagnosticMarker.FormatWorkerBuildReceiptStale(treeChanged, worktreeEvidence.DirtyPaths, uncovered));
                }
            }

            if (!worktreeEvidence.IsClean &&
                shouldCommitDirtyWorktree)
            {
                commitAttempt = _worktreeCommitter.TryCommitWorktreeEdits(
                    processRecord.WorkingDirectory,
                    DispatchWorktreeCommitter.BuildOrchestratorCommitMessage(goal, task, BuildDispatchId(goalId, taskId, task.LastDispatch!), decisionStandardOutput, decisionStandardError, worktreeEvidence.DirtyPaths),
                    worktreeEvidence.DirtyPaths);
                commitAttempted = true;
                if (commitAttempt.Succeeded &&
                    _worktreeCommitter.TryInspectGoalWorktree(
                        processRecord.WorkingDirectory,
                        goalId,
                        task.LastDispatch!.DispatchedAt,
                        out worktreeEvidence,
                        forceRefresh: true) &&
                    worktreeEvidence.IsClean && worktreeEvidence.HasRelevantCommitAfterDispatch)
                {
                    orchestratorCommitted = true;
                    hasCommittedChanges = true;
                    if (!reconcileWrapperExit && !missingWorkerBuildEvidence)
                    {
                        exitCode = 0;
                    }
                    standardErrorDiagnostic = AppendDiagnostic(
                        standardErrorDiagnostic ?? string.Empty,
                        "Orchestrator committed the worker's verified worktree edits. " +
                        $"branch={worktreeEvidence.Branch}; head={worktreeEvidence.Head}; commits_after_dispatch={worktreeEvidence.CommitsAfterDispatch}.");
                    if (sandboxCommitBlocked)
                    {
                        standardErrorDiagnostic = AppendDiagnostic(
                            standardErrorDiagnostic,
                            "Classified worker git metadata write failure as non-fatal; orchestrator commit-on-behalf is the commit path. " +
                            $"index_lock={TryResolveIndexLockPath(processRecord.WorkingDirectory)}.");
                    }
                    else if (originalExitCode != 0 && successfulWorkerResult && !provider.Capabilities.CanSelfCommit)
                    {
                        standardErrorDiagnostic = AppendDiagnostic(
                            standardErrorDiagnostic,
                            "Accepted non-zero worker exit because a complete non-failing WORKER_RESULT and dirty worktree edits were present; " +
                            "orchestrator commit-on-behalf is the commit path.");
                    }
                }
            }
            var repositoryCommitFailures = CommitRepositoryWorktreeEdits(goal, task, shouldCommitDirtyWorktree,
                decisionStandardOutput, decisionStandardError, _worktreeCommitter);
            var changeEvidence = GoalRepositoryChangeEvidence.Collect(goal, task, task.LastDispatch!.DispatchedAt, _worktreeCommitter)
                .CombineWithHome(processRecord.WorkingDirectory, worktreeEvidence);
            if (task.RequiredRole == AgentRole.Developer && exitCode == 0 &&
                InScopeIgnoredFileCheck.Evaluate(processRecord.WorkingDirectory, goal, task) is { Diagnostic.Length: > 0 } inScopeIgnored)
            {
                standardErrorDiagnostic = AppendDiagnostic(standardErrorDiagnostic ?? string.Empty, inScopeIgnored.Diagnostic);
                if (inScopeIgnored.FailsRound) exitCode = 1;
            }
            if (!orchestratorCommitted && !worktreeEvidence.IsClean && (exitCode == 0 || reconcileWrapperExit))
            {
                // Exited 0 but left uncommitted edits the orchestrator could not land (no verification
                // evidence, or the commit failed) — not acceptable.
                exitCode = 1;
                if (task.LastDispatch.SandboxLowIntegrity && !lowIntegrityConfinementEvidence)
                {
                    standardErrorDiagnostic = AppendDiagnostic(
                        standardErrorDiagnostic ?? string.Empty,
                        "Low-integrity dispatch exited 0 with a dirty worktree, but deterministic low-integrity confinement evidence was absent; refusing orchestrator commit-on-behalf.");
                }

                if (commitAttempted && !commitAttempt.Succeeded && commitAttempt.Diagnostic.Length > 0)
                {
                    standardErrorDiagnostic = AppendDiagnostic(
                        WithDeferredNoChangeDecline(kernel.GetGoal(goalId), task, worktreeEvidence, fullStandardOutput.Content is null ? null : authoritativeStandardOutput, decisionStandardError, standardErrorDiagnostic),
                        DispatchWorktreeCommitter.BuildCommitOnBehalfFailureDiagnostic(commitAttempt.Diagnostic, worktreeEvidence));
                }
                else
                {
                    standardErrorDiagnostic = AppendDiagnostic(
                        WithDeferredNoChangeDecline(kernel.GetGoal(goalId), task, worktreeEvidence, fullStandardOutput.Content is null ? null : authoritativeStandardOutput, decisionStandardError, standardErrorDiagnostic),
                        "Developer/Tester dispatch exited 0 but left the worktree dirty. " +
                        $"branch={worktreeEvidence.Branch}; head={worktreeEvidence.Head}; worktree={worktreeEvidence.WorktreeStatus}; " +
                        $"commits_after_dispatch={worktreeEvidence.CommitsAfterDispatch}; status_short={worktreeEvidence.StatusShort}.");
                }
            }
            else if (!orchestratorCommitted && worktreeEvidence.IsClean)
            {
                var hasCompletedVerification = _completionClassifier.HasCompletedVerification(
                    decisionStandardOutput,
                    decisionStandardError);
                var hasVerificationOnlyTesterCompletion = _completionClassifier.IsVerificationOnlyTesterCompletion(
                    task,
                    decisionStandardOutput,
                    hasCompletedVerification);
                var verificationRecognized =
                    hasCompletedVerification ||
                    hasVerificationOnlyTesterCompletion ||
                    _completionClassifier.HasReportedFailingVerification(decisionStandardOutput, decisionStandardError);
                var roleStillRequiresChangeEvidence =
                    dispatchRoleCapability == DispatchRoleOutputCapability.RequiresChangeEvidence;
                var allowsNoChangeCompletion = _runner.AllowsNoChangeCompletion(
                    kernel.GetGoal(goalId), task, worktreeEvidence,
                    decisionStandardOutput, fullStandardOutput.Content is not null ? authoritativeStandardOutput : null,
                    decisionStandardError, processRecord.WorkingDirectory,
                    ref standardErrorDiagnostic);
                // A typed premise stop awaits clarification, so it need not manufacture a change.
                // Preserve failed exits and let the dispatch classifier retain fault precedence.
                var hasPremiseInvalidStop = exitCode == 0 && workerResultPresent &&
                    task.RequiredRole is AgentRole.Developer or AgentRole.Tester &&
                    fullStandardOutput.Content is not null &&
                    WorkerResultBlockers.TryFindPremiseInvalidEvidence(authoritativeStandardOutput, out _);
                var requiresCommitEvidence =
                    terminalContract.AllowsCompletion && !providerCancelled &&
                    !successfulChildWithoutUsableWorkerResult &&
                    !hasPremiseInvalidStop &&
                    _completionClassifier.RequiresPostDispatchCommitEvidence(task, hasVerificationOnlyTesterCompletion) &&
                    ((roleStillRequiresChangeEvidence && !allowsNoChangeCompletion) ||
                      (!verificationRecognized &&
                       !allowsNoChangeCompletion)) &&
                    !changeEvidence.HasRelevantCommitAfterDispatch;

                if (requiresCommitEvidence)
                {
                    // The role had to land a relevant change and didn't — fail regardless of exit code
                    // (a Developer that produced nothing is a real failure, not exit-code noise).
                    exitCode = 1;
                    standardErrorDiagnostic = AppendDiagnostic(
                        AppendDiagnostic(
                            AppendDiagnostic(
                                standardErrorDiagnostic ?? string.Empty,
                                DispatchRejectionDiagnosticMarker.Format(
                                    verificationRecognized,
                                    changeEvidence.CommitsAfterDispatch,
                                    changeEvidence.ChangedPathsSummary)),
                            "Developer/Tester dispatch did not produce required relevant file-change evidence. " +
                            $"branch={worktreeEvidence.Branch}; head={worktreeEvidence.Head}; worktree={worktreeEvidence.WorktreeStatus}; " +
                            $"commits_after_dispatch={changeEvidence.CommitsAfterDispatch}; changed_paths={changeEvidence.ChangedPathsSummary}."),
                        DispatchFailureDiagnosticMarker.Format(DispatchFailureDiagnosticMarker.RequiredFileChangeEvidenceMissing));
                }
            }

            if (repositoryCommitFailures.Count > 0)
            {
                if (exitCode == 0) exitCode = 1;
                foreach (var failure in repositoryCommitFailures)
                    standardErrorDiagnostic = AppendDiagnostic(standardErrorDiagnostic ?? string.Empty, failure);
            }

            failedRoundReceipt = TryBuildFailedRoundReceipt(task, BuildDispatchId(goalId, taskId, task.LastDispatch),
                worktreeEvidence, orchestratorCommitted || (commitAttempted && commitAttempt.Succeeded),
                checkpointDisposition?.IsCheckpoint == true,
                task.LastDispatch.SandboxLowIntegrity && !lowIntegrityConfinementEvidence, processRecord.WasCancelled);

            // The subscription Tester terminal report is required independently of git
            // evidence: a relevant commit cannot replace it. Other implementation roles
            // retain their existing git/build/no-change evidence semantics.
        }
        else if (completedWorktreeInspection is { IsAvailable: false } unavailableInspection)
        {
            standardErrorDiagnostic = AppendDiagnostic(
                standardErrorDiagnostic ?? string.Empty,
                $"Completed dispatch worktree inspection {(unavailableInspection.IsUnsafe ? "unsafe" : "unavailable")}; " +
                $"unavailable_reason={unavailableInspection.UnavailableReason ?? "unknown"}; " +
                $"git_receipt={unavailableInspection.GitReceipt}.");
            if (string.Equals(unavailableInspection.UnavailableReason, "git-inspection-failed", StringComparison.Ordinal))
            {
                exitCode = 1;
                standardErrorDiagnostic = AppendDiagnostic(
                    standardErrorDiagnostic ?? string.Empty,
                    DispatchFailureDiagnosticMarker.Format(DispatchFailureDiagnosticMarker.WorktreeInspectionFailed));
            }
        }

        resourceAccounting ??= _recoveryService.ReleaseTrackedProcessJobs(processRecord);
        if (resourceAccounting is not null &&
            !resourceAccounting.Reaped &&
            DispatchProcessRecoveryService.IsDispatchHostReapCompletion(decisionStandardError))
        {
            resourceAccounting = resourceAccounting with { Reaped = true };
        }

        if (reconcileWrapperExit &&
            (dispatchRoleCapability != DispatchRoleOutputCapability.RequiresChangeEvidence || hasCommittedChanges))
        {
            var exitCodeEvidenceName = exitArtifactAlreadyExisted
                ? "observed_wrapper_exit_code"
                : "synthesized_wrapper_exit_code";
            exitCode = 0;
            wrapperExitReconciled = true;
            standardErrorDiagnostic = AppendDiagnostic(
                standardErrorDiagnostic ?? string.Empty,
                "Reconciled non-zero wrapper completion because the selected child and its complete, non-blocked WORKER_RESULT succeeded; " +
                $"{exitCodeEvidenceName}={observedExitCode}; child_exit_code=0; logical_exit_code=0.");
        }
        else if (successfulChildWithoutUsableWorkerResult)
        {
            var exitCodeEvidenceName = exitArtifactAlreadyExisted
                ? "observed_wrapper_exit_code"
                : "synthesized_wrapper_exit_code";
            standardErrorDiagnostic = AppendDiagnostic(
                AppendDiagnostic(
                    standardErrorDiagnostic ?? string.Empty,
                    "Wrapper process exited nonzero after the selected child succeeded, but no complete, usable, non-blocked WORKER_RESULT was available; " +
                    $"{exitCodeEvidenceName}={observedExitCode}; child_exit_code=0."),
                DispatchFailureDiagnosticMarker.Format(DispatchFailureDiagnosticMarker.WrapperProcessExitFailure));
        }
        if (task.LastDispatch is { } completedDispatch && !IsLocalDispatch(completedDispatch))
        {
            var reapNote = ReapWorktreeBuildDaemons(processRecord.WorkingDirectory);
            if (reapNote is not null)
            {
                standardErrorDiagnostic = AppendDiagnostic(standardErrorDiagnostic ?? string.Empty, reapNote);
            }
        }

        if (resourceAccounting is not null)
        {
            standardErrorDiagnostic = AppendDiagnostic(
                standardErrorDiagnostic ?? string.Empty,
                FormatResourceReceipt(goalId, taskId, resourceAccounting));
        }

        if (!string.IsNullOrWhiteSpace(orchestratorFailureReason))
        {
            // The exit artifact records what the dispatch host observed. A detector disposition is a
            // separate fact and must not rewrite that artifact or fabricate a contradictory exit code.
            exitCode = observedExitCode;
        }

        var humanInputQuestion = humanInputDirective.Directive?.Question;
        // Keep orchestrator-ingested plan text in the captured stdout artifact, whose path is
        // recorded below, but out of the worker decision stream and bounded verification
        // snapshot. Kernel classification reparses the snapshot for directives and blockers.
        var fullStandardError = _processLogReader.ReadComplete(processRecord.StandardErrorPath);
        var standardOutput = outputSnapshot.BoundedText;
        var standardError = AppendDiagnostic(
            AppendDiagnostic(errorSnapshot.BoundedText, standardErrorDiagnostic),
            finalPlannerRejectionDiagnostic);
        if (!exitArtifactAlreadyExisted)
        {
            _recoveryService.TryWriteExitCode(
                processRecord.ExitCodePath,
                exitCode,
                recoveryDecision is null
                    ? "orchestrator synthesized completion without a host artifact"
                    : $"orchestrator recovery action={recoveryDecision.ActionName}");
        }
        _ = DispatchExitArtifacts.TryRead(processRecord.ExitCodePath, out var exitArtifact);
        var completed = processRecord with
        {
            CompletedAt = _clock.UtcNow,
            ExitCode = exitCode,
            ResourceAccounting = resourceAccounting,
            ChildProcessId = hasChildExitRecord ? childExitRecord.ProcessId : null,
            ChildExitCode = hasChildExitRecord ? childExitRecord.ExitCode : null,
            ExitArtifactOrigin = exitArtifact?.Origin ?? DispatchExitArtifactOrigin.None,
            ExitArtifactReason = exitArtifact?.Reason
        };

        var (resultCommit, resultCommitProvenance) = CaptureResultCommit(
            processRecord.WorkingDirectory, buildEvidenceAllowsResultCommit,
            checkpointDisposition?.IsCheckpoint == true, hasCommittedChanges, orchestratorCommitted);

        // Worker-self-reported stdout bytes from the heartbeat — a flush-race-proof signal of real output.
        var heartbeatStdoutBytes = _recoveryService.TryReadHeartbeat(GetHeartbeatPath(processRecord), out var completionHeartbeat)
            ? completionHeartbeat.StandardOutputBytes
            : (long?)null;
        // The bounded snapshot is diagnostic evidence only. A scope declaration can steer a
        // Developer into a bounded revision, so it must come from the authoritative artifact.
        var assignedScopeComplete = fullStandardOutput.Content is { } assignedScopeOutput &&
            WorkerResultBlockers.TryGetAssignedScopeComplete(assignedScopeOutput, out var scopeComplete, out _)
                ? scopeComplete : (bool?)null;

        var verification = WorkerWriteIntegrityObservationProducer.Enrich(task, _runner.WriteIntegrityLabeler, new TaskVerificationRecord(
            processRecord.Command,
            processRecord.WorkingDirectory,
            exitCode,
            standardOutput,
            standardError,
            completed.CompletedAt.Value,
            StandardOutputPath: processRecord.StandardOutputPath,
            StandardErrorPath: processRecord.StandardErrorPath,
            WorkerResultPresent: workerResultPresent,
            HasCommittedChanges: hasCommittedChanges,
            HeartbeatStandardOutputBytes: heartbeatStdoutBytes,
            ProviderFailureKind: providerFailureKind,
            HumanInputQuestion: humanInputQuestion, PlannerEvidenceBatch: humanInputDirective,
            DispatchStartedAt: processRecord.StartedAt,
            ChildProcessId: completed.ChildProcessId,
            ChildExitCode: completed.ChildExitCode,
            OrchestratorFailureReason: orchestratorFailureReason,
            HumanInputQuestionFingerprint: humanInputDirective.Directive?.QuestionFingerprint,
            HumanInputBlockerFingerprint: humanInputDirective.Directive?.BlockerFingerprint,
            HumanInputKind: humanInputDirective.Directive?.Kind,
            HumanInputEvidenceOwner: humanInputDirective.Directive?.EvidenceOwner, HumanInputStoreReference: humanInputDirective.Directive?.StoreReference,
            ObservedRootExitCode: observedExitCode,
            ReconciledToSuccess: wrapperExitReconciled,
            ReconciliationOriginRule: wrapperExitReconciled ? reconciliationOriginRule : null,
            FullStandardOutput: fullStandardOutput.Content,
            FullStandardError: fullStandardError.Content,
            FullStandardOutputUnavailableReason: fullStandardOutput.UnavailableReason,
            FullStandardErrorUnavailableReason: fullStandardError.UnavailableReason,
            PlannerCandidateDivergence: plannerCandidateDivergence,
            AssignedScopeComplete: assignedScopeComplete,
            EvidenceSource: VerificationEvidenceSource.Dispatch,
            // Planner evidence binds its brief through host dispatch provenance as well.
            DispatchAttemptAt: requiresTerminalContract || task.RequiredRole == AgentRole.Planner
                ? dispatchAttempt?.DispatchedAt : null));

        var outcome = new DispatchRefreshOutcome(
            completed,
            verification,
            resultCommit,
            resultCommitProvenance,
            recoveryDecision,
            providerFailureKind,
            new DispatchDiagnosticPayload(exitCode, standardOutput, standardError),
            AutoRequeueDisposition: DispatchAutoRequeueDisposition.FromInterruptedWorkCheckpoint(
                checkpointDisposition,
                recoveryDecision),
            ProviderUsage: providerUsage.Usage,
            ProviderUsageUnavailableReason: providerUsage.UnavailableReason,
            DispatchAttemptAt: contextReceiptAttemptAt, ReceiptlessUsageAttemptAt: ReceiptlessUsageAttemptAt(dispatchAttempt), FailedRoundReceipt: failedRoundReceipt,
            SkillsReceipt: ResolveSkillsReceipt(goalId, taskId, dispatchAttempt, processRecord.StandardOutputPath, verification));
        _processLogReader.Evict(processRecord);
        return outcome;
    }

    private static TaskDispatchRecord? ResolveDispatchAttempt(TaskSpec task, TaskProcessRecord processRecord) =>
        task.DispatchHistory
            .Where(dispatch => dispatch.DispatchedAt <= processRecord.StartedAt)
            .OrderByDescending(dispatch => dispatch.DispatchedAt)
            .FirstOrDefault();

    private static string TryResolveIndexLockPath(string workingDirectory)
    {
        try
        {
            return GoalWorktreeGitMetadata.Inspect(workingDirectory).IndexLockPath;
        }
        catch
        {
            return Path.Combine(workingDirectory, ".git", "index.lock");
        }
    }

    private string? ReapWorktreeBuildDaemons(string workingDirectory) =>
        WorktreeBuildDaemonReaper.Reap(workingDirectory, _findBuildDaemons, _tryKillBuildDaemon);

    private static bool TryReadChildExitRecord(
        string? path,
        out DispatchProcessHost.DispatchChildExitRecord record)
    {
        record = new DispatchProcessHost.DispatchChildExitRecord(0, 0, default);
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return false;
        }

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var parsed = JsonSerializer.Deserialize<DispatchProcessHost.DispatchChildExitRecord>(
                stream,
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
            if (parsed is null || parsed.ProcessId <= 0)
            {
                return false;
            }

            record = parsed;
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static TaskIntentSuppressionReceipt? TryReadIntentSuppression(
        GoalId goalId, TaskId taskId, TaskSpec task, TaskProcessRecord process, int exitCode,
        out bool suppressedPrimaryWithStartedSamples)
    {
        suppressedPrimaryWithStartedSamples = false;
        if (exitCode != TaskIntentAdmission.SuppressedExitCode ||
            !DispatchExitArtifacts.TryRead(process.ExitCodePath, out var artifact) ||
            artifact.Origin != DispatchExitArtifactOrigin.Native) return null;
        const string outputSuffix = ".out.log";
        if (!process.StandardOutputPath.EndsWith(outputSuffix, StringComparison.Ordinal)) return null;
        var parameters = DispatchProcessHost.ReadParameters(process.StandardOutputPath[..^outputSuffix.Length] + ".dispatch.json");
        if (parameters.TaskIntentAdmission is not { } admission || admission.GoalId != goalId.Value ||
            admission.TaskId != taskId.Value) return null;
        var store = new SqliteOperatorIntentStore(admission.StorePath, Path.GetDirectoryName(admission.StorePath)!, readOnly: true);
        var receipt = store.GetSuppression(process.StandardOutputPath);
        if (receipt is null || receipt.GoalId != goalId.Value || receipt.TaskId != taskId.Value ||
            receipt.PreparedVersion != admission.Version) return null;

        // A denied primary says nothing about samples whose gates were released first.
        // Only actual child admission records, not supervisor launch artifacts, prove start.
        var allSamplesDenied = true;
        foreach (var sample in PlannerSampleDispatcher.CreateArtifacts(process.StandardOutputPath,
                     task.RequiredRole == AgentRole.Planner ? task.LastDispatch?.PlannerSampleCount ?? 1 : 1))
        {
            if (store.HasLaunch(sample.StandardOutputPath))
            {
                suppressedPrimaryWithStartedSamples = true;
                allSamplesDenied = false;
                continue;
            }
            var denial = store.GetSuppression(sample.StandardOutputPath);
            if (denial is null || denial.GoalId != goalId.Value || denial.TaskId != taskId.Value ||
                denial.PreparedVersion != admission.Version)
                allSamplesDenied = false;
        }
        return allSamplesDenied ? receipt : null;
    }

    private static bool RequiresDurableResearchArtifact(Goal goal, TaskSpec researcher)
    {
        var researcherIndex = goal.Tasks.ToList().FindIndex(candidate => candidate.Id == researcher.Id);
        var plannerIndex = goal.Tasks.ToList().FindIndex(candidate => candidate.RequiredRole == AgentRole.Planner);
        return researcherIndex >= 0 && plannerIndex > researcherIndex;
    }

    private static bool RequiresDurablePlanArtifact(Goal goal, TaskSpec planner)
    {
        var plannerIndex = goal.Tasks.ToList().FindIndex(candidate => candidate.Id == planner.Id);
        return ScoutRoundPolicy.IsScoutPlanner(goal, planner) ||
            (plannerIndex > 0 && goal.Tasks.Take(plannerIndex).Any(candidate => candidate.RequiredRole == AgentRole.Researcher));
    }

    private static bool RequiresDurableScoutResearchArtifact(Goal goal, TaskSpec planner)
        => goal.RefinedSpec is not null && ScoutRoundPolicy.IsScoutPlanner(goal, planner);

    private static bool TryPersistScoutResearch(string selectedSourcePath, string standardOutputPath, out string diagnostic)
    {
        var captured = ResearcherOutputContract.ReadCapturedOutputTail(selectedSourcePath);
        var normalized = MarkdownHeadingNormalizer.SeparateInlineAtxHeadings(captured);
        var planStart = PlannerOutputContract.FindFirstRequiredHeadingIndex(normalized);
        var researchOutput = planStart >= 0 ? normalized[..planStart] : normalized;
        var contract = ResearcherOutputContract.Resolve(researchOutput);
        if (!contract.Succeeded || contract.Research is null)
        {
            diagnostic = AppendDiagnostic(
                contract.Diagnostic.Replace("Retry Researcher", "Retry Planner", StringComparison.Ordinal),
                DispatchFailureDiagnosticMarker.Format(DispatchFailureDiagnosticMarker.ResearcherOutputContractRejected));
            return false;
        }

        if (!ResearcherOutputContract.TryPersistDurableReceipt(
                standardOutputPath,
                contract.Research,
                out var persistenceDiagnostic))
        {
            diagnostic = AppendDiagnostic(
                $"Scout research output contract could not persist the accepted artifact: {persistenceDiagnostic}. Retry Planner for contract repair.",
                DispatchFailureDiagnosticMarker.Format(DispatchFailureDiagnosticMarker.ResearcherArtifactPersistenceFailed));
            return false;
        }

        diagnostic = string.Empty;
        return true;
    }

    private static (string? Commit, string? Provenance) CaptureResultCommit(
        string workingDirectory, bool buildEvidenceAllowsResultCommit, bool isCheckpoint,
        bool hasCommittedChanges, bool orchestratorCommitted)
    {
        // A result SHA is completion credit, so require certified build evidence first.
        var commit = !buildEvidenceAllowsResultCommit || isCheckpoint ? null : TryGetWorktreeHead(workingDirectory);
        var provenance = commit is not null && hasCommittedChanges
            ? orchestratorCommitted ? "orchestrator" : "worker"
            : null;
        return (commit, provenance);
    }

    private OrchestratorBuildEvidenceResolution ResolveWorkerBuildInvocation(AgentOrchestratorKernel kernel,
        GoalId goalId, TaskId taskId, IReadOnlyList<string> changedPaths, IReadOnlyList<string> dirtyPaths,
        string workerOutput, bool completeWorkerResult, Func<OrchestratorBuildEvidenceResolution> reconcileBuild,
        ref string? orchestratorFailureReason)
    {
        if (_runner.WorkerBuildInvocations is null)
        {
            // Missing apparatus must deny verification without inventing a native worker exit.
            orchestratorFailureReason ??= "worker-build-consumer-unavailable";
            return OrchestratorBuildEvidenceCheck.RetainUnverifiedBusyClaim(
                new(true, false, "worker_build_host=unavailable; enrolled dispatch cannot use legacy worker receipts"), workerOutput);
        }
        var receipt = _runner.WorkerBuildInvocations.Consume(kernel, goalId, taskId, changedPaths, dirtyPaths);
        var host = new OrchestratorBuildEvidenceResolution(receipt.MissingEvidence, receipt.CompilerFailed, receipt.Diagnostic)
            { Applicability = receipt.Applicability };
        if (receipt.Gap == WorkerBuildEvidenceGap.Rejected)
        {
            orchestratorFailureReason ??= "worker-build-authority-rejected";
            return host with { Diagnostic = AppendDiagnostic(host.Diagnostic,
                "completion_build_hold=authority-rejected; operator decision required before build execution") };
        }
        var task = kernel.GetTask(goalId, taskId);
        var infrastructure = WorkerBuildInfrastructureEvidence.Classify(task);
        var missingOutcome = receipt.Gap == WorkerBuildEvidenceGap.MissingApplicableInvocation &&
            task.RequiredRole is AgentRole.Developer or AgentRole.Tester && completeWorkerResult &&
            WorkerResultBlockers.TryGetAssignedScopeComplete(workerOutput, out var scopeComplete, out _) && scopeComplete;
        if (infrastructure == WorkerBuildInfrastructureBlocker.None && !missingOutcome)
            return OrchestratorBuildEvidenceCheck.RetainUnverifiedBusyClaim(host, workerOutput);
        if (!receipt.MissingEvidence || receipt.CompilerFailed ||
            WorkerResultBlockers.TryGetAssignedScopeComplete(workerOutput, out var complete, out _) && !complete)
            return host;
        if (_completionBuildEvidence is null)
        {
            orchestratorFailureReason ??= "completion-build-coordinator-unavailable";
            return host with { Diagnostic = AppendDiagnostic(host.Diagnostic,
                "completion_build_hold=coordinator-unavailable; operator decision required to restore existing build execution authority") };
        }

        var build = reconcileBuild();
        var diagnostic = missingOutcome
            ? "worker_build_recovery=missing-applicable-outcome; transport_cause=unknown; automatic_attempt_limit=1"
            : $"worker_build_infrastructure={infrastructure}";
        if (build.Pending || build.BuildFailed ||
            !build.FailsRound && build.Operation is { Phase: CompletionBuildEvidencePhase.Success } operation &&
            operation.DispatchAt == task.LastDispatch?.DispatchedAt)
            return build with { Diagnostic = AppendDiagnostic(build.Diagnostic, diagnostic), Applicability = receipt.Applicability };

        // A receipt alone cannot certify a protocol-1 round, nor can another dispatch's operation.
        return host with { Diagnostic = AppendDiagnostic(AppendDiagnostic(host.Diagnostic, build.Diagnostic), diagnostic) };
    }

    private static DateTimeOffset? ReceiptlessUsageAttemptAt(TaskDispatchRecord? dispatch) =>
        dispatch?.ContextPackageReceipt is null ? dispatch?.DispatchedAt : null;

    private static FailedRoundCheckpointReceipt? TryBuildFailedRoundReceipt(
        TaskSpec task, string dispatchId, GoalWorktreeDispatchEvidence evidence,
        bool committed, bool checkpointed, bool lowIntegrityRefused, bool wasCancelled)
    {
        // Final failure is checked in ApplyRefreshOutcome, after wrapper reconciliation.
        if (task.RequiredRole != AgentRole.Developer || committed || checkpointed ||
            lowIntegrityRefused || wasCancelled || evidence.CommitsAfterDispatch != 0 ||
            evidence.IsClean || evidence.DirtyPaths.Count == 0)
            return null;
        return new(dispatchId, evidence.DirtyStateHash, evidence.DirtyPaths.ToArray());
    }

    private string WithDeferredNoChangeDecline(
        Goal goal, TaskSpec task, GoalWorktreeDispatchEvidence worktreeEvidence,
        string? standardOutput, string standardError, string? diagnostic)
    {
        if (standardOutput is null) return diagnostic ?? string.Empty;
        DeveloperDeferredNoChangeQualifier.TryQualify(
            goal, task, worktreeEvidence.Head, worktreeEvidence.IsClean,
            worktreeEvidence.HasRelevantCommitAfterDispatch,
            standardOutput, standardError, _completionClassifier, out _, out var declineCode);
        return _runner.AppendDeferredNoChangeDecline(task, standardOutput, diagnostic, declineCode);
    }

    private string ResolveSkillsReceipt(GoalId goal, TaskId task, TaskDispatchRecord? dispatch,
        string stdoutPath, TaskVerificationRecord verification)
    {
        IReadOnlyList<string>? read = null;
        try { read = _runner.SkillTranscripts.Read(dispatch, stdoutPath); }
        catch (Exception) { /* A failed observation is still recorded as unavailable. */ }
        IReadOnlyList<string> claimed = [];
        try
        {
            if (new WorkerResultContractParser().TryParseWorkerResultContract(verification, out var fields) &&
                fields.TryGetValue("skills", out var value)) claimed = SkillNames(value.Split(','));
        }
        catch (Exception) { /* Claims are advisory telemetry. */ }
        return $"{RoundValueSkillSlice.NotePrefix}goal={goal.Value[..Math.Min(8, goal.Value.Length)]} task={task.Value[..Math.Min(8, task.Value.Length)]} " +
            $"selected={List(dispatch?.SelectedSkills ?? [])} read={(read is null ? "unavailable" : List(read))} claimed={List(claimed)}";
    }

    private static string List(IEnumerable<string> skills)
    {
        var names = SkillNames(skills);
        return names.Count == 0 ? "none" : string.Join(',', names);
    }

    private static IReadOnlyList<string> SkillNames(IEnumerable<string> names) => names.Where(n => n is not null).Select(n => n.Trim())
        .Where(n => !n.Equals("none", StringComparison.OrdinalIgnoreCase) &&
            Regex.IsMatch(n, @"\A[a-z0-9][a-z0-9_-]*\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        .Select(n => n.ToLowerInvariant()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
}
