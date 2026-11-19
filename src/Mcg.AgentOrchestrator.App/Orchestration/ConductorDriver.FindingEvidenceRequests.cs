using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorDriver
{
    internal Action<GoalId>? CheckpointFindingEvidenceCompletion { get; set; }

    internal bool TryBuildFindingEvidenceRequest(Goal goal, TaskSpec task, ConductorAutonomyPolicy policy,
        out FailedGoalFindingObservation decision) => FindingRoundBatchCoordinator.Observe(this, goal, task, policy, out decision);

    internal bool TryBuildFindingEvidenceRequestCore(
        Goal goal,
        TaskSpec requestingTask,
        ConductorAutonomyPolicy policy,
        FindingRoundBatchCoordinator.ExecutionContext execution, Action<FindingRoundRedCoverage.Delivery> observeDelivery,
        Action<FindingRoundRedCoverage.Delivery?, FailedGoalFindingObservation?> stagePendingSuppressions,
        out FailedGoalFindingObservation? reusedRedDecision,
        out FailedGoalFindingObservation decision)
    {
        decision = FailedGoalFindingObservation.None;
        reusedRedDecision = null;
        var round = execution.Round;
        var mergedFindings = requestingTask.LastVerification?.MergedReviewFindings ?? [];
        var candidateSha = _getPreReviewEvidenceContext(goal).CandidateSha?.Trim();
        var candidateShaAvailable = ConductorGitRevisionReader.IsValid(candidateSha);
        var telemetryCandidateSha = candidateShaAvailable ? candidateSha! : "unavailable";
        var requestingFindings = round.Findings
            .Where(finding =>
                finding.State == ReviewFindingState.Open &&
                finding.EvidenceRequest is not null)
            .ToArray();
        if (requestingFindings.Length == 0)
        {
            return false;
        }
        var findingRoundFingerprint = BuildFindingRoundFingerprint(requestingTask, round);
        var findingEvidenceEngineSettings = execution.HomeSettings;
        var executionBasisIdentity = execution.HomeBasis;
        var batchBasisIdentity = execution.BatchBasis;
        var batchIdentity = new FindingEvidenceBatchIdentity(requestingTask.Id, telemetryCandidateSha,
            findingRoundFingerprint, batchBasisIdentity);
        FindingRoundBatchCoordinator.ResumeTerminalRefusals(this, goal, requestingTask, batchIdentity, execution, requestingFindings);
        var continuationFindingIds = new HashSet<string>(StringComparer.Ordinal);
        if (UsesQualifiedReviewerContinuation(goal, requestingTask) &&
            FindingEvidenceContinuationCoordinator.TrySelectFindingEvidenceContinuation(this, goal, requestingTask, out var continuation, out _))
        {
            foreach (var selection in continuation!.Selections.DistinctBy(selection => selection.FindingId))
            {
                FindingRoundBatchCoordinator.RecordCoverage(this, goal, requestingTask, telemetryCandidateSha,
                    findingRoundFingerprint, batchBasisIdentity, selection.RequestIdentity, selection.FindingId, selection.ReceiptId);
                continuationFindingIds.Add(selection.FindingId);
            }
        }

        void ReattachCoveredEvidence(ReviewFinding finding, IReadOnlyList<FindingEvidenceReceipt> receipts,
            string identity, string reason)
        {
            _recordFindingEvidenceRequestDispositions(goal.Id, requestingTask.Id,
                [new(finding.StableId, identity, "covered-by-receipt", reason,
                    FindingEvidenceRequestDecision.CoveredByReceipt, candidateSha, findingRoundFingerprint,
                    SourceReceiptIds: receipts.Select(receipt => receipt.ReceiptId).ToArray(),
                    ExecutionBasisIdentity: executionBasisIdentity)]);
            FindingEvidenceCoverageReattachment.Reattach(goal, requestingTask,
                ReviewFindingConvergence.ResolveMergedFinding(mergedFindings, round, finding.StableId) ?? finding,
                receipts, identity, reason, _recordFindingEvidenceOutcome, _recordFindingEvidenceRequest);
            FindingRoundBatchCoordinator.RecordCoverage(this, goal, requestingTask, telemetryCandidateSha,
                findingRoundFingerprint, batchBasisIdentity, identity, finding.StableId, receipts[0].ReceiptId);
        }

        var hasReviewerDecision = ReviewerPassingFindingEvidenceRouter.TryObserve(this, goal, requestingTask, policy,
            out var reviewerDecision, ReattachCoveredEvidence);
        if (hasReviewerDecision && (!FindingRoundBatchAccounting.HasOpenBatch(goal, batchIdentity) ||
            FindingRoundBatchAccounting.Summarize(goal, requestingTask, batchIdentity, "reviewer-delivery").IsSettled))
        {
            decision = reviewerDecision;
            return true;
        }

        if (requestingTask.RequiredRole == AgentRole.Reviewer && !hasReviewerDecision)
        {
            requestingFindings = ReviewerFindingEvidenceSuppressionRouting.Apply(this, goal, requestingTask,
                mergedFindings.Count > 0 ? mergedFindings : round.Findings,
                requestingFindings, candidateSha,
                new(requestingTask.Id, telemetryCandidateSha, findingRoundFingerprint, batchBasisIdentity),
                _recordFindingEvidenceRequestDispositions, _recordFindingEvidenceSuppressed);
            if (requestingFindings.Length == 0) return false;
        }

        var preTesterCoverage = PreTesterCoverageComposition.ReadCoverage(this, goal, telemetryCandidateSha, executionBasisIdentity);
        var groups = new List<FindingEvidenceRequestGroup>();
        var repositoryTargets = new Dictionary<string, KeyedRepositoryEvidenceTarget>(StringComparer.Ordinal);
        (KeyedRepositoryEvidenceTarget? Target, string Failure) ResolveTarget(Goal _, string key) => execution.Targets[key];
        var nonExecutableIds = FindingRoundBatchAccounting.NonExecutableFindingIds(goal,
            new(requestingTask.Id, telemetryCandidateSha, findingRoundFingerprint, batchBasisIdentity));
        var normalizationRefused = false;
        var reusedGreenReceipt = false;
        var reattachedReceiptIds = new List<string>();
        foreach (var finding in requestingFindings)
        {
            if (nonExecutableIds.Contains(finding.StableId) || continuationFindingIds.Contains(finding.StableId)) continue;
            if (!FindingEvidenceRequestNormalization.TryNormalizeForGoal(
                    finding.EvidenceRequest!, goal, findingEvidenceEngineSettings, ResolveTarget,
                    (project, requestedClass) =>
                        _resolveFindingEvidenceSiblingClasses(goal, project, requestedClass),
                    out var typedRequest, out var request, out var repositoryTarget,
                    out var refusalReason, out var refusalDetail))
            {
                normalizationRefused = true;
                RecordNotHonoured(
                    goal.Id, requestingTask, finding, refusalReason, refusalDetail,
                    repositoryTarget?.CandidateHead ?? telemetryCandidateSha,
                    repositoryTarget?.EngineSettings ?? findingEvidenceEngineSettings);
                FindingRoundBatchCoordinator.RecordRefusal(this, goal, requestingTask, finding, telemetryCandidateSha,
                    findingRoundFingerprint, batchBasisIdentity, refusalReason);
                if (refusalReason == FindingEvidenceNotHonouredReason.ExecutorUnavailable)
                    FindingRoundBatchCoordinator.RecordUnavailableFinding(this, goal, requestingTask, finding, telemetryCandidateSha,
                        findingRoundFingerprint, batchBasisIdentity, refusalDetail);
                continue;
            }

            var identity = BuildFindingEvidenceIdentity(typedRequest);
            var requestCandidateSha = repositoryTarget?.CandidateHead ?? telemetryCandidateSha;
            var requestSettings = repositoryTarget?.EngineSettings ?? findingEvidenceEngineSettings;
            var requestBasis = repositoryTarget is null ? executionBasisIdentity
                : FindingRoundBatchCoordinator.ExecutionContext.TargetBasis(repositoryTarget);
            if (repositoryTarget is not null) repositoryTargets[identity] = repositoryTarget;
            IReadOnlyList<FindingEvidenceReceipt> reusedSourceReceipts = [];
            string? coverageDecisionReason = null;
            var mergedFinding = ReviewFindingConvergence.ResolveMergedFinding(
                mergedFindings, round, finding.StableId);
            var priorOutcome = mergedFinding?.EvidenceOutcome;
            if (priorOutcome is not null && repositoryTarget is null &&
                (execution.Targets.Count == 0 || priorOutcome.Reason != FindingEvidenceNotHonouredReason.SupersededByActionableRed) &&
                IsPermanentFindingEvidenceRefusal(priorOutcome) && FocusedEvidenceInventoryIdentity.StillBinds(priorOutcome, requestSettings))
            {
                FindingRoundBatchCoordinator.RecordRefusal(this, goal, requestingTask, finding, telemetryCandidateSha,
                    findingRoundFingerprint, batchBasisIdentity, priorOutcome.Reason ?? FindingEvidenceNotHonouredReason.Unknown);
                continue;
            }

            if (mergedFinding is not null &&
                FindingEvidenceRequestCompletion.FindCurrentGreenReceipt(
                        requestingTask,
                        mergedFinding,
                        typedRequest,
                        requestCandidateSha,
                        findingRoundFingerprint,
                         requestBasis,
                          repositoryTarget is null ? preTesterCoverage : []) is { } currentGreen)
            {
                // Reconcile qualified receipts that predate batch observations before skipping execution.
                FindingRoundBatchCoordinator.RecordCurrentGreenReceipt(this, goal, requestingTask, telemetryCandidateSha,
                    findingRoundFingerprint, batchBasisIdentity, identity, finding.StableId, currentGreen);
                continue;
            }

            if (repositoryTarget is null && PreTesterCoverageComposition.TryResumeRemainder(this, goal, requestingTask, telemetryCandidateSha,
                    findingRoundFingerprint, identity, executionBasisIdentity, typedRequest, out var pendingRemainder, out var pendingSources))
            {
                typedRequest = pendingRemainder;
                request = string.Join("; ", typedRequest.Selections.Select(FormatFindingEvidenceSelection));
                reusedSourceReceipts = pendingSources;
                coverageDecisionReason = pendingSources.Count == 0 ? "pre-tester-authority-missing" : "partial";
            }
            else if (repositoryTarget is null && typedRequest.NegativeControl is null && TryResolveFindingEvidenceCoverage(
                    requestingTask,
                    typedRequest,
                    telemetryCandidateSha,
                    executionBasisIdentity,
                    out var coverageReceipts,
                    out var uncoveredSelections,
                    out var coverageReason, preTesterCoverage))
            {
                reusedGreenReceipt = true;
                if (uncoveredSelections.Count == 0)
                {
                    ReattachCoveredEvidence(finding, coverageReceipts, identity, coverageReason);
                    reattachedReceiptIds.AddRange(coverageReceipts.Select(receipt => receipt.ReceiptId));
                    continue;
                }

                typedRequest = typedRequest with { Selections = uncoveredSelections };
                request = string.Join("; ", uncoveredSelections.Select(FormatFindingEvidenceSelection));
                reusedSourceReceipts = coverageReceipts;
                coverageDecisionReason = coverageReason;
            }

            var retainedRed = reusedRedDecision;
            if (repositoryTarget is null && !FindingRoundRedCoverage.HasCompletedRedRequest(requestingTask, finding, typedRequest, identity,
                    telemetryCandidateSha, findingRoundFingerprint, executionBasisIdentity, goal.Timeline) &&
                TryRouteReusableRedFindingEvidence(
                    goal, requestingTask, finding, typedRequest, request, identity,
                    telemetryCandidateSha, executionBasisIdentity, policy, findingRoundFingerprint, batchBasisIdentity,
                    delivery => stagePendingSuppressions(delivery, retainedRed), out var redDecision))
            {
                if (redDecision.Kind is not (FailedGoalFindingObservationKind.FindingActionableRed or
                    FailedGoalFindingObservationKind.FindingOperatorEvidenceRequired))
                {
                    decision = redDecision;
                    return true;
                }
                if (redDecision.Kind == FailedGoalFindingObservationKind.FindingActionableRed)
                    reusedRedDecision ??= redDecision;
                continue;
            }

            var groupIndex = groups.FindIndex(group => string.Equals(group.Identity, identity, StringComparison.Ordinal));
            if (groupIndex < 0)
            {
                groups.Add(new FindingEvidenceRequestGroup(
                    identity,
                    request,
                    typedRequest,
                    [finding],
                    reusedSourceReceipts,
                    coverageDecisionReason));
            }
            else
            {
                groups[groupIndex].Findings.Add(finding);
            }
        }

        if (groups.Count == 0 && normalizationRefused &&
            !FindingRoundBatchAccounting.HasOpenBatch(goal,
                new(requestingTask.Id, telemetryCandidateSha, findingRoundFingerprint, batchBasisIdentity)))
        {
            decision = BuildQualifiedFindingEvidenceDeliveryRetry(
                goal, requestingTask, telemetryCandidateSha, requestingFindings, [],
                "Every current evidence request was refused during normalization; typed refusal details were attached.");
            return true;
        }

        if (groups.Count == 0 && reusedRedDecision is not null)
        {
            decision = reusedRedDecision;
            return true;
        }

        if (groups.Count == 0 && reusedGreenReceipt)
        {
            decision = BuildReceiptClosureOrDeliveryRetry(
                goal, requestingTask, telemetryCandidateSha, requestingFindings,
                reattachedReceiptIds.Distinct(StringComparer.Ordinal).ToArray(),
                "Previously executed green evidence still matches the candidate and normalized request; its receipt was reattached without rerunning tests.", policy);
            return true;
        }

        var reviewerRoute = hasReviewerDecision ? reviewerDecision : null;
        var homeGroups = groups.Where(group => !repositoryTargets.ContainsKey(group.Identity)).ToArray();
        if (homeGroups.Length > 0 && TryExecuteFindingEvidenceGroups(goal, requestingTask, policy, homeGroups,
                requestingFindings, candidateSha, findingRoundFingerprint, executionBasisIdentity, findingEvidenceEngineSettings,
                null, telemetryCandidateSha, batchBasisIdentity, groups.Except(homeGroups).ToArray(), reusedRedDecision,
                reviewerRoute, observeDelivery, stagePendingSuppressions, out decision))
            return true;
        foreach (var partition in groups.Where(group => repositoryTargets.ContainsKey(group.Identity))
                     .GroupBy(group => repositoryTargets[group.Identity].Key).OrderBy(partition => partition.Key, StringComparer.Ordinal))
        {
            var target = repositoryTargets[partition.First().Identity];
            if (TryExecuteFindingEvidenceGroups(goal, requestingTask, policy, partition.ToArray(),
                    partition.SelectMany(group => group.Findings).ToArray(), target.CandidateHead,
                    findingRoundFingerprint, FindingRoundBatchCoordinator.ExecutionContext.TargetBasis(target), target.EngineSettings,
                    target, telemetryCandidateSha, batchBasisIdentity,
                    groups.Except(partition).ToArray(), reusedRedDecision, reviewerRoute, observeDelivery, stagePendingSuppressions, out decision))
                return true;
        }
        if (reusedRedDecision is not null || hasReviewerDecision)
        {
            decision = reusedRedDecision ?? reviewerDecision;
            return true;
        }
        return false;
    }

    private bool TryExecuteFindingEvidenceGroups(
        Goal goal, TaskSpec requestingTask, ConductorAutonomyPolicy policy,
        IReadOnlyList<FindingEvidenceRequestGroup> groups, IReadOnlyList<ReviewFinding> requestingFindings,
        string? candidateSha, string findingRoundFingerprint, string executionBasisIdentity,
        AcceptanceGateEngineSettings findingEvidenceEngineSettings, KeyedRepositoryEvidenceTarget? repositoryTarget,
        string batchCandidateSha, string batchBasisIdentity, IReadOnlyList<FindingEvidenceRequestGroup> otherRepositoryGroups,
        FailedGoalFindingObservation? reusedRedDecision, FailedGoalFindingObservation? reviewerRoute,
        Action<FindingRoundRedCoverage.Delivery> observeDelivery,
        Action<FindingRoundRedCoverage.Delivery?, FailedGoalFindingObservation?> stagePendingSuppressions,
        out FailedGoalFindingObservation decision)
    {
        decision = FailedGoalFindingObservation.None;
        var candidateShaAvailable = ConductorGitRevisionReader.IsValid(candidateSha);
        var telemetryCandidateSha = candidateShaAvailable ? candidateSha! : "unavailable";
        void SupersedeRemaining(IEnumerable<FindingEvidenceBatch> pending, FindingEvidenceReceipt redReceipt) =>
            FindingRoundBatchSchedule.RecordSuperseded(goal, requestingTask,
                pending.Concat(FindingEvidenceRoundPlanner.BuildFindingEvidenceBatches(otherRepositoryGroups)),
                telemetryCandidateSha, redReceipt,
                new(requestingTask.Id, batchCandidateSha, findingRoundFingerprint, batchBasisIdentity),
                _recordFindingEvidenceOutcome, _recordFindingEvidenceRequest);
        var plan = FindingRoundBatchSchedule.PlanRequests(goal, requestingTask, groups, telemetryCandidateSha,
            findingRoundFingerprint, executionBasisIdentity, repositoryTarget is not null);
        FindingRoundBatchCoordinator.RecordSettledPlan(this, goal, requestingTask, batchCandidateSha,
            findingRoundFingerprint, batchBasisIdentity, plan, telemetryCandidateSha, findingEvidenceEngineSettings);
        FailedGoalFindingObservation? redRoute = null;
        FindingEvidenceReceipt? actionableReceipt = null;
        foreach (var (batch, covering) in plan.RedCovered)
        {
            var attribution = TryAttributeActionableCandidateRed(goal, telemetryCandidateSha, SelectedExecutionGateMissDetector.Observe(GateMissDetector, goal.Id, requestingTask.Id, covering.ReceiptId, covering.Arms!, _recordTaskNote), batch);
            if (attribution is not null)
            {
                actionableReceipt ??= covering;
                redRoute ??= BuildActionableCandidateRedDecision(goal, requestingTask, telemetryCandidateSha,
                    covering.ReceiptId, attribution, $"request_identity={batch.Identity};");
                FindingRoundBatchCoordinator.RecordActionableRedOutcomes(this, goal, requestingTask, batchCandidateSha,
                    findingRoundFingerprint, batchBasisIdentity, batch, covering, validatedReplayCoverage: true);
                continue;
            }
            else if (CandidateRedBaselineInheritance.TryBuildUnattributableRedEscalation(
                    telemetryCandidateSha, covering.Arms!, batch, covering.ReceiptId, out var unavailable))
            {
                FindingRoundBatchCoordinator.RecordOutcome(this, goal, requestingTask, batchCandidateSha, findingRoundFingerprint,
                    batchBasisIdentity, batch, "unavailable", covering.ReceiptId, unavailable.Evidence + $" request_identity={batch.Identity};");
                continue;
            }
            FindingRoundBatchCoordinator.RecordOutcome(this, goal, requestingTask, batchCandidateSha, findingRoundFingerprint,
                batchBasisIdentity, batch, "executed", covering.ReceiptId);
        }
        if (actionableReceipt is not null)
        {
            SupersedeRemaining(plan.Executable, actionableReceipt);
            decision = redRoute!;
            return true;
        }
        var redCoverage = FindingRoundRedCoverage.Evaluate(plan.Executable, requestingTask, goal.Timeline,
            telemetryCandidateSha, findingRoundFingerprint, executionBasisIdentity);
        redCoverage.RecordSkips(goal, requestingTask, telemetryCandidateSha, findingRoundFingerprint, executionBasisIdentity,
            _recordFindingEvidenceRequestDispositions, _recordFindingEvidenceOutcome, _recordFindingEvidenceRequest);
        foreach (var item in redCoverage.Skipped)
            FindingRoundBatchCoordinator.RecordOutcome(this, goal, requestingTask, batchCandidateSha,
                findingRoundFingerprint, batchBasisIdentity, item.Batch, "covered", item.Receipts[0].ReceiptId);
        var batches = redCoverage.Remaining;
        var runnable = batches.FirstOrDefault();
        if (runnable is null)
        {
            if (plan.RequiredCap(goal, requestingTask) is { } capped)
            {
                decision = FindingRoundBatchSchedule.BuildRunCapDecision(goal, requestingTask, capped.Batch, capped.Count,
                    capped.ReceiptId, telemetryCandidateSha, findingRoundFingerprint, _recordFindingEvidenceRequest);
                return true;
            }
            if (plan.RedCovered.Count == 0 && redCoverage.Skipped.Count == 0) return false;
            decision = reusedRedDecision ?? BuildReceiptClosureOrDeliveryRetry(goal, requestingTask, telemetryCandidateSha,
                plan.CoveredFindings.Concat(redCoverage.Findings).ToArray(),
                plan.CoveredReceiptIds.Concat(redCoverage.ReceiptIds).Distinct(StringComparer.Ordinal).ToArray(),
                plan.CoveredEvidence + "; " + redCoverage.Evidence, policy);
            return true;
        }

        if (!candidateShaAvailable)
        {
            foreach (var group in batches)
            foreach (var finding in group.Findings)
            {
                RecordNotHonoured(
                    goal.Id, requestingTask, finding, FindingEvidenceNotHonouredReason.CandidateShaMissing,
                    "No validated candidate SHA was available for the requested evidence run.",
                    telemetryCandidateSha);
            }
            decision = BuildQualifiedFindingEvidenceDeliveryRetry(
                goal, requestingTask, telemetryCandidateSha, requestingFindings, [],
                "Evidence requests could not run because the candidate SHA was unavailable.");
            return true;
        }

        if (repositoryTarget is null && !_focusedEvidenceRunnerConfigured)
        {
            foreach (var batch in batches)
            {
                const string reason = "No focused evidence executor was configured.";
                foreach (var finding in batch.Findings)
                    RecordNotHonoured(goal.Id, requestingTask, finding, FindingEvidenceNotHonouredReason.ExecutorUnavailable,
                        reason, telemetryCandidateSha);
                FindingRoundBatchCoordinator.RecordOutcome(this, goal, requestingTask, batchCandidateSha, findingRoundFingerprint,
                    batchBasisIdentity, batch, "unavailable", null, reason);
            }
            return false;
        }
        var initialRequestDispositions = FindingEvidenceRoundPlanner.BuildInitialRequestDispositions(
            batches, runnable, candidateSha!, findingRoundFingerprint, executionBasisIdentity);
        _recordFindingEvidenceRequestDispositions(goal.Id, requestingTask.Id, initialRequestDispositions);
        var requestContext = new ConductorFocusedEvidenceRequestContext(
            findingRoundFingerprint,
            CreateFindingEvidenceBatchId(candidateSha!, findingRoundFingerprint, policy.Name, runnable.Identity),
            initialRequestDispositions, NegativeControl: runnable.TypedRequest.NegativeControl, RevertPaths: runnable.TypedRequest.RevertPaths,
            Mutation: runnable.TypedRequest.Mutation, RepositoryTarget: repositoryTarget, ExecutionBasisIdentity: executionBasisIdentity);
        void StageRerunSuppressions(FindingRoundRedCoverage.Delivery delivery) =>
            stagePendingSuppressions(delivery, reusedRedDecision);
        if (TryResumeUnconfirmedCandidateRed(
                goal, requestingTask, policy, runnable, candidateSha!, findingRoundFingerprint,
                requestContext, out decision, StageRerunSuppressions))
        {
            decision = FindingRoundBatchCoordinator.CompleteMissingBaseline(this, goal, requestingTask, runnable,
                batches.Count > 1, batchCandidateSha, findingRoundFingerprint, batchBasisIdentity, decision, reusedRedDecision);
            return true;
        }
        if (!TryRestorePendingBaselineCandidate(goal, runnable.Request, candidateSha!, requestContext,
                out var evidence, out var evidenceAttempt) && !TryReconcileFocusedEvidenceAttempt(
                goal,
                policy,
                runnable.Request,
                candidateSha!,
                "finding-requested",
                requestContext,
                out evidence,
                out evidenceAttempt,
                out decision, out var attemptKind, existingAttempt: null, deferRoundReconciliation: true))
        {
            decision = FindingRoundBatchCoordinator.ObserveUnfinishedExecution(this, goal, requestingTask, runnable,
                batches.Count > 1, telemetryCandidateSha, executionBasisIdentity,
                new(requestingTask.Id, batchCandidateSha, findingRoundFingerprint, batchBasisIdentity),
                attemptKind, evidenceAttempt, decision, reusedRedDecision);
            return true;
        }

        if (!evidence.Accepted)
        {
            var reason = evidence.Rejection?.Code switch
            {
                FocusedEvidenceRejectionCode.UnsupportedProject =>
                    FindingEvidenceNotHonouredReason.UnsupportedProject,
                FocusedEvidenceRejectionCode.SourceDiscoveryFailure =>
                    FindingEvidenceNotHonouredReason.SelectionApparatusFailure,
                _ => FindingEvidenceNotHonouredReason.UnparseableSelection
            };
            var detail = evidence.Rejection is null
                ? evidence.Summary
                : $"{evidence.Rejection.Detail}; offending_filter='{evidence.Rejection.OffendingToken}'";
            decision = FindingRoundBatchCoordinator.RecordExecutionFailure(this, goal, requestingTask, runnable,
                batches.Count > 1, telemetryCandidateSha, findingRoundFingerprint, executionBasisIdentity,
                reason, detail, batchCandidateSha, batchBasisIdentity, reusedRedDecision, evidenceAttempt!, findingEvidenceEngineSettings);
            return true;
        }

        var initialArmReceipts = (evidence.Arms ?? [])
            .Select(CreateFindingEvidenceArmReceipt)
            .ToArray();
        var reconciliationDeferred = CanDeferFindingEvidenceReconciliation(candidateSha!, evidence);
        if (!TryResolveMissingBaseline(
                goal, requestingTask, policy, runnable, candidateSha!, findingRoundFingerprint, requestContext,
                evidence, initialArmReceipts,
                out evidence, out var armReceipts, out var receiptIdentity, out decision,
                stageRerunSuppressions: StageRerunSuppressions))
        {
            decision = FindingRoundBatchCoordinator.CompleteMissingBaseline(this, goal, requestingTask, runnable,
                batches.Count > 1, batchCandidateSha, findingRoundFingerprint, batchBasisIdentity, decision, reusedRedDecision);
            return true;
        }
        var receiptId = CreateFindingEvidenceReceiptId(
            candidateSha!, findingRoundFingerprint, receiptIdentity, executionBasisIdentity);
        var actionableCandidateRed = TryAttributeActionableCandidateRed(
            goal, candidateSha!, SelectedExecutionGateMissDetector.Observe(GateMissDetector, goal.Id, requestingTask.Id, receiptId, armReceipts, _recordTaskNote), runnable);
        var receipt = new FindingEvidenceReceipt(
            receiptId, candidateSha!, runnable.TypedRequest, evidence.Accepted, evidence.IsValidEvidence,
            evidence.Summary, armReceipts, FindingRoundFingerprint: findingRoundFingerprint,
            ExecutionBasisIdentity: executionBasisIdentity, NegativeControlOutcome: evidence.NegativeControlOutcome,
            RevertPathsRejection: evidence.RevertPathsRejection);
        var requestDispositions = FindingEvidenceRequestCompletion.BuildDispositions(
            initialRequestDispositions, runnable, receipt, FormatFindingEvidenceSelection,
            actionableCandidateRed is null ? null : batches.Skip(1));
        receipt = receipt with { RequestDispositions = requestDispositions };
        var apparatusFailure = evidence.OutcomeReason == FindingEvidenceOutcomeReason.ApparatusFailure &&
            !armReceipts.Any(arm => arm is { Arm: FindingEvidenceArm.Candidate, Disposition: FindingEvidenceArmDisposition.Red });
        var completions = new List<(ReviewFinding Finding, PreTesterCoverageComposition.Completion Completion)>();
        if (!apparatusFailure)
        {
            foreach (var finding in runnable.Findings)
            {
                var member = runnable.Members.Single(candidate => candidate.Findings.Contains(finding));
                var completion = PreTesterCoverageComposition.Complete(this, goal, requestingTask, finding, member,
                    receipt, candidateSha!, executionBasisIdentity, findingRoundFingerprint, evidence.OutcomeReason);
                receipt = completion.Receipt;
                completions.Add((finding, completion));
            }
        }
        if (actionableCandidateRed is not null)
            receipt = FindingEvidenceRequestCompletion.DeferIncompleteMembers(receipt);

        // Until the dispositions persist, the focused attempt can be reconciled again and
        // no task receipt can bypass that reconciliation on the next tick.
        requestDispositions = receipt.RequestDispositions!;
        if (!FindingEvidenceRequestCompletion.TryPersistDispositions(_focusedEvidenceAttemptCoordinator, evidenceAttempt,
                findingRoundFingerprint, receiptId, requestDispositions, out decision))
            return true;

        var checkpointGreen = reconciliationDeferred && receipt.Passed && !apparatusFailure;
        if (reconciliationDeferred && !checkpointGreen && !apparatusFailure)
            _focusedEvidenceAttemptCoordinator.MarkReconciled(evidenceAttempt!);

        FindingRoundRedCoverage.Delivery? completedDelivery = null;
        Func<FailedGoalFindingObservation> StageCompletion()
        {
            var (route, deliverable) = FindingRoundRedCoverage.StageCompletion(this, goal, requestingTask, batches.Skip(1).ToArray(),
                runnable, plan, receipt, candidateSha!, findingRoundFingerprint, executionBasisIdentity,
                _recordFindingEvidenceRequestDispositions, _recordFindingEvidenceOutcome, _recordFindingEvidenceRequest,
                batchCandidateSha, batchBasisIdentity, policy, reusedRedDecision);
            if (deliverable is not null)
            {
                completedDelivery = deliverable with { ReviewerRoute = reviewerRoute };
                observeDelivery(completedDelivery);
            }
            return route;
        }

        if (apparatusFailure)
        {
            FindingRoundBatchCoordinator.CheckpointFailedAttemptRun(this, goal, requestingTask, runnable,
                candidateSha!, executionBasisIdentity, receiptId, FindingEvidenceNotHonouredReason.SelectionApparatusFailure, evidenceAttempt!);
            _recordFindingEvidenceRequestDispositions(goal.Id, requestingTask.Id, requestDispositions);
            foreach (var finding in runnable.Findings)
            {
                _recordFindingEvidenceOutcome(
                    goal.Id,
                    requestingTask.Id,
                    finding.StableId,
                    new FindingEvidenceOutcome(
                        Honoured: false,
                        ReceiptId: receiptId,
                        Reason: FindingEvidenceNotHonouredReason.SelectionApparatusFailure,
                        Detail: evidence.Summary,
                        ResultReason: FindingEvidenceOutcomeReason.ApparatusFailure),
                    receipt);
                _recordFindingEvidenceRequest(
                    goal.Id,
                    requestingTask.Id,
                    $"finding-evidence disposition=not-honoured; role={requestingTask.RequiredRole}; " +
                    $"task_id={requestingTask.Id}; finding_id={finding.StableId}; candidate_sha={candidateSha}; " +
                    $"receipt_id={receiptId}; reason=selection-apparatus-failure; " +
                    $"detail={TrimForConductorMessage(evidence.Summary)}");
            }
            if (reconciliationDeferred)
                _focusedEvidenceAttemptCoordinator.MarkReconciled(evidenceAttempt!);
            decision = StageCompletion()();
            return true;
        }
        var incomplete = completions.Any(item => item.Completion.Incomplete);
        Func<FailedGoalFindingObservation>? routeAfterCheckpoint = null;
        FindingEvidenceCompletionPublisher.Publish(this, goal, requestingTask, receipt, completions, FormatFocusedEvidenceResult(evidence),
            checkpointGreen ? evidenceAttempt : null, () =>
            {
                if (!incomplete) routeAfterCheckpoint = StageCompletion();
                stagePendingSuppressions(completedDelivery, reusedRedDecision);
            });

        if (actionableCandidateRed is not null)
        {
            SupersedeRemaining(batches.Skip(1), receipt);
            FindingRoundBatchCoordinator.RecordActionableRedOutcomes(this, goal, requestingTask, batchCandidateSha,
                findingRoundFingerprint, batchBasisIdentity, runnable, receipt);
            decision = BuildActionableCandidateRedDecision(
                goal, requestingTask, candidateSha!, receiptId, actionableCandidateRed);
            return true;
        }
        if (CandidateRedBaselineInheritance.TryBuildUnattributableRedEscalation(
                candidateSha!, armReceipts, runnable, receiptId, out decision))
        {
            FindingRoundBatchCoordinator.RecordOutcome(this, goal, requestingTask, batchCandidateSha, findingRoundFingerprint,
                batchBasisIdentity, runnable, "unavailable", receiptId, decision.Evidence);
            decision = reusedRedDecision ?? decision;
            return true;
        }
        if (incomplete)
        {
            decision = FailedGoalFindingObservation.Observed(FailedGoalFindingObservationKind.FindingEvidencePending,
                "Focused execution retained; reused pre-Tester coverage is invalid or incomplete. Uncovered selections remain pending.");
            return true;
        }

        decision = (routeAfterCheckpoint ?? StageCompletion())();
        return true;
    }

    internal static bool TryNormalizeFindingEvidenceRequest(
        FindingEvidenceRequest request,
        AcceptanceGateEngineSettings engineSettings,
        Func<string, string, IReadOnlyList<string>> resolveSiblingClasses,
        out FindingEvidenceRequest normalized,
        out string executorRequest,
        out FindingEvidenceNotHonouredReason refusalReason,
        out string refusalDetail) =>
        FindingEvidenceRequestNormalization.TryNormalize(request, engineSettings, resolveSiblingClasses,
            out normalized, out executorRequest, out refusalReason, out refusalDetail);

    internal static string FormatFindingEvidenceSelection(FindingEvidenceSelection selection) =>
        selection.TestProject + ":" + selection.TestClass;

    internal static string BuildFindingEvidenceIdentity(FindingEvidenceRequest request) =>
        FindingEvidenceExecutionClassifier.BuildRequestIdentity(request);

    internal static string CreateFindingEvidenceReceiptId(
        string candidateSha,
        string findingRoundFingerprint,
        string identity) =>
        "finding-evidence-" + Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes($"{candidateSha}:{findingRoundFingerprint}:{identity}")))[..24];

    internal static string CreateFindingEvidenceReceiptId(
        string candidateSha,
        string findingRoundFingerprint,
        string identity,
        string? executionBasisIdentity) =>
        CreateFindingEvidenceReceiptId(candidateSha, findingRoundFingerprint,
            executionBasisIdentity is null ? identity : $"finding-evidence-basis-v1:{identity}:{executionBasisIdentity}");

    internal static string CreateFindingEvidenceBatchId(
        string candidateSha,
        string findingRoundFingerprint,
        string policyName,
        string identity) =>
        "evidence-batch-" + Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(
                $"{candidateSha}:{findingRoundFingerprint}:{policyName}:{identity}")))
            .ToLowerInvariant()[..16];

    internal static ConductorAdvanceOutcome.Held FocusedEvidencePendingHeld(
        GoalLifecycleState state,
        FailedGoalFindingObservation decision,
        ConductorParallelAcceptanceAttemptDecisionKind? kind) =>
        new(state, decision.Evidence)
        {
            Owner = kind == ConductorParallelAcceptanceAttemptDecisionKind.Running
                ? ConductorHoldOwner.BackgroundAttempt
                : ConductorHoldOwner.None
        };

}
