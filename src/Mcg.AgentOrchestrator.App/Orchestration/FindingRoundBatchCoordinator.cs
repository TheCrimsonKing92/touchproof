using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class FindingRoundBatchCoordinator
{
    internal sealed record ExecutionContext(
        ReviewFindingRound Round, AcceptanceGateEngineSettings HomeSettings, string HomeBasis, string BatchBasis,
        IReadOnlyDictionary<string, (KeyedRepositoryEvidenceTarget? Target, string Failure)> Targets)
    {
        internal static string TargetBasis(KeyedRepositoryEvidenceTarget target) =>
            FindingEvidenceReviewContext.Hash(JsonSerializer.Serialize(new[] { target.Key, target.CandidateHead.ToLowerInvariant(),
                target.IntegrationBranch, target.ManifestIdentity,
                ConductorDriver.BuildFindingEvidenceExecutionBasisIdentity(target.EngineSettings) }));

        internal bool Matches(FindingEvidenceReceipt receipt, FindingEvidenceBatchIdentity identity)
        {
            if (receipt.FindingRoundFingerprint != identity.RoundFingerprint) return false;
            if (FindingEvidenceRequestNormalization.IsHomeRepository(receipt.Request.Repository))
                return receipt.ExecutionBasisIdentity == HomeBasis &&
                    receipt.CandidateSha.Equals(identity.CandidateSha, StringComparison.OrdinalIgnoreCase);
            return Targets.TryGetValue(receipt.Request.Repository!, out var resolution) && resolution.Target is { } target &&
                receipt.ExecutionBasisIdentity == TargetBasis(target) &&
                receipt.CandidateSha.Equals(target.CandidateHead, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static ExecutionContext ResolveExecution(ConductorDriver driver, Goal goal, ReviewFindingRound round)
    {
        var settings = driver._getFindingEvidenceEngineSettings(goal);
        var home = ConductorDriver.BuildFindingEvidenceExecutionBasisIdentity(settings);
        var keys = round.Findings.Where(finding => finding.State == ReviewFindingState.Open && finding.EvidenceRequest is not null)
            .Select(finding => finding.EvidenceRequest!.Repository)
            .Where(key => !FindingEvidenceRequestNormalization.IsHomeRepository(key) && goal.Repositories?.Repositories.Contains(key) == true)
            .Select(key => key!.ToLowerInvariant()).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
        var targets = keys.ToDictionary(key => key, key => driver.ResolveKeyedEvidenceTarget(goal, key), StringComparer.OrdinalIgnoreCase);
        var batch = targets.Count == 0 ? home : FindingEvidenceReviewContext.Hash(JsonSerializer.Serialize(
            new[] { home }.Concat(targets.Select(pair => pair.Key + ":" +
                (pair.Value.Target is { } target ? ExecutionContext.TargetBasis(target) : "unavailable")))));
        return new(round, settings, home, batch, targets);
    }

    internal static ReviewFinding[] PendingFindings(TaskSpec task, string? candidate) =>
        (task.LastVerification?.MergedReviewFindings ?? [])
            .Where(finding => finding.State == ReviewFindingState.Open &&
                FindingEvidenceExecutionClassifier.Classify(task, finding, candidate) == FindingEvidenceExecutionState.PendingExecution)
            .ToArray();

    internal static bool TrySourceRepair(Goal goal, TaskSpec task, string candidate,
        IReadOnlyList<string> receiptIds, IReadOnlyList<ReviewFinding> findings, out FailedGoalFindingObservation route)
    {
        route = FailedGoalFindingObservation.None;
        if (task.RequiredRole != AgentRole.Tester || !findings.Any(finding =>
                finding.Category is FindingCategory.Correctness or FindingCategory.TestCoverage or
                    FindingCategory.SpecCompliance or FindingCategory.CodeQuality) ||
            (!findings.Any(finding => finding.EvidenceRequest is null) &&
             FindingEvidenceReceiptSelector.SuppliedReceiptsPassAtCandidate(task, candidate, receiptIds)) ||
            ConductorDriver.FindUpstreamDeveloperTask(goal, task) is not { } developer) return false;
        route = FailedGoalFindingObservation.Routed(FailedGoalFindingObservationKind.FindingRouteObserved,
            developer.Id, ConductorDriver.BuildFailedGoalAttemptIdentity(developer),
            $"FINDING_BATCH_DRAINED route=developer; candidate_sha={candidate}; receipt_ids={string.Join(',', receiptIds)}; " +
            "An open source finding remains after the evidence batch settled.", null,
            observedCause: RetryCause.NewSourceFinding);
        return true;
    }

    internal static ConductorAdvanceOutcome.Held ClosedHold(GoalLifecycleState state, FailedGoalRecoveryIdentity identity)
    {
        const string reason = "Finding batch closure was already applied or its identity changed.";
        return new(state, reason)
        {
            Decision = FailedGoalRecoveryDecisionRecords.StaleRecoveryFacts(identity, reason)
        };
    }

    private static bool PolicySelects(ConductorDriver driver, Goal goal, ConductorAutonomyPolicy policy,
        FailedGoalFindingObservation selection)
    {
        var facts = driver.BuildFailedGoalRecoveryFacts(goal, policy, GoalLifecycleState.Failed)
            .WithReviewContractObservation(driver.TryObserveReviewContractRecovery(goal, out var contract) ? contract : null)
            .WithVerifyingFindingObservation(selection);
        var decision = FailedGoalRecoveryPolicy.Evaluate(facts);
        return decision.Action == FailedGoalRecoveryAction.FindingRetry && decision.Identity.TaskId == selection.TargetTaskId;
    }
    internal static (bool Applied, string? Conflict) ApplyRetry(ConductorDriver driver, Goal goal,
        FindingEvidenceBatchClosure? closure, Func<bool> isCurrent, Func<string?> retry)
    {
        if (closure is null) return (true, retry());
        string? conflict = null;
        var applied = TryApply(driver, goal, closure, isCurrent, () => (conflict = retry()) is null);
        return (applied, conflict);
    }

    internal static bool TryApply(ConductorDriver driver, Goal goal, FindingEvidenceBatchClosure closure,
        Func<bool> isCurrent, Func<bool> apply, bool useRetainedVerification = false)
    {
        bool Validate() => Identity(driver, goal, goal.Tasks.Single(task => task.Id == closure.Identity.RequestingTaskId),
            out _, useRetainedVerification) == closure.Identity && isCurrent();
        return FindingEvidenceBatchClosureAdmission.TryApply(goal, closure, Validate, apply);
    }

    internal static bool TryResume(ConductorDriver driver, Goal goal, TaskSpec task, ConductorAutonomyPolicy policy,
        out FailedGoalFindingObservation observation)
    {
        observation = FailedGoalFindingObservation.None;
        return Identity(driver, goal, task) is { } identity &&
            (FindingRoundBatchAccounting.HasOpenBatch(goal, identity) || FindingRoundBatchAccounting.IsClosed(goal, identity)) &&
            Observe(driver, goal, task, policy, out observation);
    }

    internal static FailedGoalFindingObservation CompleteMissingBaseline(ConductorDriver driver, Goal goal, TaskSpec task,
        FindingEvidenceBatch batch, bool morePending, string candidate, string round, string basis,
        FailedGoalFindingObservation observation, FailedGoalFindingObservation? reusedActionableRed) =>
        observation.Kind switch
        {
            FailedGoalFindingObservationKind.FindingOperatorEvidenceRequired =>
                SettleUnavailable(driver, goal, task, batch, morePending, candidate, round, basis, observation.Evidence, reusedActionableRed),
            // A GREEN rerun settles this request. An earlier actionable RED still owns the route.
            FailedGoalFindingObservationKind.FindingEvidenceDeliveryRecorded or FailedGoalFindingObservationKind.FindingRetryCapReached
                => reusedActionableRed ?? observation,
            _ => observation
        };

    internal static FailedGoalFindingObservation SettleUnavailable(ConductorDriver driver, Goal goal, TaskSpec task,
        FindingEvidenceBatch batch, bool morePending, string candidate, string round, string basis, string reason,
        FailedGoalFindingObservation? reusedActionableRed)
    {
        RecordOutcome(driver, goal, task, candidate, round, basis, batch, "unavailable", null, reason);
        var identity = new FindingEvidenceBatchIdentity(task.Id, candidate, round, basis);
        return RouteUnavailable(goal, task, identity, morePending, reason, reusedActionableRed);
    }

    private static FailedGoalFindingObservation RouteUnavailable(Goal goal, TaskSpec task, FindingEvidenceBatchIdentity identity,
        bool morePending, string reason, FailedGoalFindingObservation? reusedActionableRed)
    {
        if (reusedActionableRed is not null && !morePending &&
            FindingRoundBatchAccounting.Summarize(goal, task, identity, "settlement-check").IsSettled &&
            FindingRoundBatchAccounting.RequiredUnavailable(goal, task, identity).Length == 0)
            return reusedActionableRed;

        return FailedGoalFindingObservation.Observed(morePending ? FailedGoalFindingObservationKind.FindingEvidencePending
            : FailedGoalFindingObservationKind.FindingOperatorEvidenceRequired,
            morePending ? "A focused evidence request is unavailable; other distinct requests remain pending. " + reason
                : "Focused evidence batch settled with unavailable required evidence. " + reason);
    }

    internal static void RecordRefusal(ConductorDriver driver, Goal goal, TaskSpec task, ReviewFinding finding,
        string candidate, string round, string basis, FindingEvidenceNotHonouredReason reason)
    {
        if (!FindingEvidenceExecutionClassifier.IsPermanentRefusal(new(false, Reason: reason))) return;
        RecordUnavailableFinding(driver, goal, task, finding, candidate, round, basis,
            FindingEvidenceNotHonouredReasonJsonConverter.ToWireValue(reason));
    }

    internal static void RecordUnavailableFinding(ConductorDriver driver, Goal goal, TaskSpec task, ReviewFinding finding,
        string candidate, string round, string basis, string reason)
    {
        var identity = new FindingEvidenceBatchIdentity(task.Id, candidate, round, basis);
        FindingRoundBatchAccounting.Record(goal, identity,
            new(identity.Key, "unavailable", ConductorDriver.BuildFindingEvidenceIdentity(finding.EvidenceRequest!) + ":" + finding.StableId,
                [finding.StableId], Eligibility: reason), driver._recordFindingEvidenceRequest);
    }

    internal static FailedGoalFindingObservation ObserveUnfinishedExecution(ConductorDriver driver, Goal goal, TaskSpec task,
        FindingEvidenceBatch batch, bool morePending, string candidate, string basis, FindingEvidenceBatchIdentity identity,
        ConductorParallelAcceptanceAttemptDecisionKind? attemptKind, ConductorParallelAcceptanceAttempt? attempt,
        FailedGoalFindingObservation observation, FailedGoalFindingObservation? reusedActionableRed)
    {
        // Capacity and cancellation can defer execution without spending the request allowance.
        var executionFault = attemptKind == ConductorParallelAcceptanceAttemptDecisionKind.TerminalWithoutRun &&
            attempt is not null && ConductorDriver.IsFocusedEvidenceExecutionFault(attempt.Outcome);
        if (!executionFault && observation.Kind != FailedGoalFindingObservationKind.FindingOperatorEvidenceRequired)
            return observation;

        const FindingEvidenceNotHonouredReason reason = FindingEvidenceNotHonouredReason.RunFailed;
        return RecordExecutionFailure(driver, goal, task, batch, morePending, candidate, identity.RoundFingerprint, basis,
            reason, observation.Evidence, identity.CandidateSha, identity.ExecutionBasisIdentity, reusedActionableRed, attempt!);
    }

    internal static FailedGoalFindingObservation RecordExecutionFailure(ConductorDriver driver, Goal goal, TaskSpec task,
        FindingEvidenceBatch batch, bool morePending, string candidate, string round, string basis,
        FindingEvidenceNotHonouredReason reason, string detail, string batchCandidate, string batchBasis,
        FailedGoalFindingObservation? reusedActionableRed, ConductorParallelAcceptanceAttempt attempt,
        AcceptanceGateEngineSettings? settings = null)
    {
        // Rejected and failed executions consume the same existing allowance as completed runs.
        var receiptId = ConductorDriver.CreateFindingEvidenceReceiptId(candidate, round, batch.Identity, basis);
        var identity = new FindingEvidenceBatchIdentity(task.Id, batchCandidate, round, batchBasis);
        // Checkpoint the original members before per-finding writes can change the next tick's plan.
        var refusal = FindingEvidenceExecutionClassifier.IsPermanentRefusal(new(false, Reason: reason))
            ? new TerminalRefusal(attempt.AttemptId, batch.TypedRequest.Repository, receiptId, reason, detail,
                FailedAttemptMembers(batch))
            : null;
        lock (goal)
        {
            CheckpointFailedAttemptRun(driver, goal, task, batch, candidate, basis, receiptId, reason, attempt,
                refusal is null ? null : $"{RefusalCheckpointPrefix}{identity.Key}; refusal={JsonSerializer.Serialize(refusal)}");

            FailedGoalFindingObservation decision;
            if (refusal is not null)
            {
                PublishTerminalRefusal(driver, goal, task, identity, refusal, batch.Findings, candidate, settings);
                decision = RouteUnavailable(goal, task, identity, morePending, detail, reusedActionableRed);
            }
            else
            {
                foreach (var finding in batch.Findings)
                    driver.RecordNotHonoured(goal.Id, task, finding, reason, detail, candidate, settings);
                decision = FailedGoalFindingObservation.Observed(FailedGoalFindingObservationKind.FindingEvidencePending,
                    "Focused evidence execution failed transiently; the request remains pending within its run allowance. " + detail);
            }
            if (!morePending && reusedActionableRed is null && !FindingRoundBatchAccounting.HasOpenBatch(goal, identity) &&
                TryBuildFailedExecutionDelivery(goal, task, identity.CandidateSha,
                    batch.Findings.Select(finding => finding.StableId).ToHashSet(StringComparer.Ordinal), out var delivery))
                decision = delivery;
            driver.FocusedEvidenceAttemptCoordinator.MarkReconciled(attempt);
            return decision;
        }
    }

    internal static void CheckpointFailedAttemptRun(ConductorDriver driver, Goal goal, TaskSpec task,
        FindingEvidenceBatch batch, string candidate, string basis, string receiptId,
        FindingEvidenceNotHonouredReason reason, ConductorParallelAcceptanceAttempt attempt, string? membership = null)
    {
        var marker = $"{FailedAttemptRunPrefix}{attempt.AttemptId};";
        lock (goal)
        {
            if (goal.Timeline.Any(item => item.Kind == ProgressKind.FindingEvidenceRunRecorded && item.TaskId == task.Id &&
                    item.Message.StartsWith(marker, StringComparison.Ordinal))) return;

            membership ??= $"{FailedAttemptMembersPrefix}{JsonSerializer.Serialize(FailedAttemptMembers(batch))}";
            driver._checkpointFindingEvidenceRun(goal.Id, task.Id,
                $"{marker} finding_id={batch.Findings[0].StableId}; candidate_sha={candidate}; " +
                $"receipt_id={receiptId}; execution_basis={basis}{membership}; reason={FindingEvidenceNotHonouredReasonJsonConverter.ToWireValue(reason)};");
        }
    }

    private static FindingRoundBatchAccounting.Member[] FailedAttemptMembers(FindingEvidenceBatch batch) =>
        batch.Members.Select(member => new FindingRoundBatchAccounting.Member(member.Identity,
            member.Findings.Select(finding => finding.StableId).ToArray())).ToArray();

    internal const string FailedAttemptRunPrefix = "finding-evidence failed-attempt; attempt_id=";
    internal const string FailedAttemptMembersPrefix = "; request_members=";
    private const string RefusalCheckpointPrefix = "; refusal_batch=";

    private sealed record TerminalRefusal(string AttemptId, string? Repository, string ReceiptId,
        FindingEvidenceNotHonouredReason Reason, string Detail, FindingRoundBatchAccounting.Member[] Members);

    internal static void ResumeTerminalRefusals(ConductorDriver driver, Goal goal, TaskSpec task, FindingEvidenceBatchIdentity identity,
        ExecutionContext execution, IReadOnlyList<ReviewFinding> findings)
    {
        var marker = $"{RefusalCheckpointPrefix}{identity.Key}; refusal=";
        lock (goal)
        {
            var refusals = new List<TerminalRefusal>();
            foreach (var item in goal.Timeline)
            {
                if (item.TaskId != task.Id || item.Kind != ProgressKind.FindingEvidenceRunRecorded ||
                    !item.Message.StartsWith(FailedAttemptRunPrefix, StringComparison.Ordinal)) continue;
                var start = item.Message.IndexOf(RefusalCheckpointPrefix, StringComparison.Ordinal);
                if (start < 0 || !item.Message.AsSpan(start).StartsWith(marker, StringComparison.Ordinal)) continue;
                var end = item.Message.LastIndexOf("; reason=", StringComparison.Ordinal);
                refusals.Add(JsonSerializer.Deserialize<TerminalRefusal>(item.Message[(start + marker.Length)..end]) ??
                    throw new InvalidOperationException("Focused evidence refusal checkpoint is null."));
            }
            if (refusals.Count == 0) return;

            var attempts = driver.FocusedEvidenceAttemptCoordinator.GetUnreconciledAttempts([goal.Id.Value]);
            foreach (var refusal in refusals)
            {
                var target = FindingEvidenceRequestNormalization.IsHomeRepository(refusal.Repository)
                    ? null : execution.Targets[refusal.Repository!].Target ??
                        throw new InvalidOperationException("The repository for a current focused evidence refusal is unavailable.");
                PublishTerminalRefusal(driver, goal, task, identity, refusal, findings,
                    target?.CandidateHead ?? identity.CandidateSha, target?.EngineSettings ?? execution.HomeSettings);
                var attempt = attempts.SingleOrDefault(item => item.AttemptId == refusal.AttemptId);
                if (attempt is not null) driver.FocusedEvidenceAttemptCoordinator.MarkReconciled(attempt);
            }
        }
    }

    private static void PublishTerminalRefusal(ConductorDriver driver, Goal goal, TaskSpec task, FindingEvidenceBatchIdentity identity,
        TerminalRefusal refusal, IReadOnlyList<ReviewFinding> findings, string candidate, AcceptanceGateEngineSettings? settings)
    {
        var recorded = FindingRoundBatchAccounting.Read(goal, identity)
            .Where(item => item.Kind == "unavailable" && item.ReceiptId == refusal.ReceiptId)
            .Select(item => item.Identity).ToHashSet(StringComparer.Ordinal);
        var reason = FindingEvidenceNotHonouredReasonJsonConverter.ToWireValue(refusal.Reason);
        foreach (var member in refusal.Members)
        {
            if (recorded.Contains(member.RequestIdentity)) continue;
            foreach (var findingId in member.FindingIds)
                driver.RecordNotHonoured(goal.Id, task, findings.Single(finding => finding.StableId == findingId),
                    refusal.Reason, refusal.Detail, candidate, settings);
            FindingRoundBatchAccounting.Record(goal, identity, new(identity.Key, "unavailable", member.RequestIdentity, member.FindingIds,
                refusal.ReceiptId, Eligibility: $"{reason}: {refusal.Detail}"), driver._recordFindingEvidenceRequest);
        }
    }

    internal static void RecordSettledPlan(ConductorDriver driver, Goal goal, TaskSpec task, string candidate,
        string round, string basis, FindingRoundBatchPlan plan, string executionCandidate, AcceptanceGateEngineSettings settings)
    {
        foreach (var unavailable in plan.Unavailable)
            RecordOutcome(driver, goal, task, candidate, round, basis, unavailable.Batch, "unavailable", unavailable.ReceiptId,
                $"Request {unavailable.Batch.Identity} completed {unavailable.Count} runs; last_receipt_id={unavailable.ReceiptId}.");
        foreach (var (batch, receipt) in plan.Passed)
            RecordExecutedOutcomes(driver, goal, task, candidate, round, basis, batch, receipt);
        foreach (var refusal in plan.Refusals)
        {
            var reason = FindingEvidenceNotHonouredReasonJsonConverter.ToWireValue(refusal.Reason);
            var detail = $"Focused evidence attempt {refusal.AttemptId} recorded a permanent refusal ({reason}).";
            lock (goal)
            {
                foreach (var finding in refusal.Batch.Findings)
                    driver.RecordNotHonoured(goal.Id, task, finding, refusal.Reason, detail, executionCandidate, settings);
                RecordOutcome(driver, goal, task, candidate, round, basis, refusal.Batch, "unavailable", refusal.ReceiptId, detail);
                var attempt = driver.FocusedEvidenceAttemptCoordinator.GetUnreconciledAttempts([goal.Id.Value])
                    .SingleOrDefault(item => item.AttemptId == refusal.AttemptId);
                if (attempt is not null) driver.FocusedEvidenceAttemptCoordinator.MarkReconciled(attempt);
            }
        }
    }

    internal static void RecordOutcome(ConductorDriver driver, Goal goal, TaskSpec task, string candidate, string round, string basis,
        FindingEvidenceBatch batch, string kind, string? receiptId, string? reason = null)
    {
        var identity = new FindingEvidenceBatchIdentity(task.Id, candidate, round, basis);
        foreach (var member in batch.Members)
            FindingRoundBatchAccounting.Record(goal, identity,
                new(identity.Key, kind, member.Identity, member.Findings.Select(finding => finding.StableId).ToArray(), receiptId,
                    Eligibility: reason),
                driver._recordFindingEvidenceRequest);
    }

    internal static void RecordRerunOutcome(ConductorDriver driver, Goal goal, TaskSpec task,
        FindingEvidenceBatch batch, FindingEvidenceReceipt receipt)
    {
        if (receipt.RequestDispositions is not { } dispositions || batch.Members.Any(member =>
                !dispositions.Any(disposition => disposition.RequestIdentity == member.Identity &&
                    disposition.Decision is (FindingEvidenceRequestDecision.Executed or FindingEvidenceRequestDecision.CoveredByReceipt) &&
                    disposition.SourceReceiptIds?.Contains(receipt.ReceiptId, StringComparer.Ordinal) == true)))
            return;

        if (Identity(driver, goal, task, out var execution) is { } identity && execution!.Matches(receipt, identity))
            RecordExecutedOutcomes(driver, goal, task, identity.CandidateSha, identity.RoundFingerprint,
                identity.ExecutionBasisIdentity, batch, receipt);
    }

    internal static void RecordActionableRedOutcomes(ConductorDriver driver, Goal goal, TaskSpec task,
        string candidate, string round, string basis, FindingEvidenceBatch batch, FindingEvidenceReceipt receipt,
        bool validatedReplayCoverage = false)
    {
        var identity = new FindingEvidenceBatchIdentity(task.Id, candidate, round, basis);
        foreach (var member in batch.Members)
        {
            var outcomes = (receipt.RequestDispositions ?? []).Where(disposition =>
                    disposition.RequestIdentity == member.Identity &&
                    member.Findings.Any(finding => finding.StableId == disposition.FindingStableId) &&
                    disposition.SourceReceiptIds?.Contains(receipt.ReceiptId, StringComparer.Ordinal) == true)
                .Select(disposition => (disposition.FindingStableId, Kind: disposition.Decision switch
                {
                    FindingEvidenceRequestDecision.Executed => "executed",
                    FindingEvidenceRequestDecision.CoveredByReceipt => "covered",
                    FindingEvidenceRequestDecision.DeferredToNextCandidate => "deferred",
                    _ => null
                }))
                .Where(outcome => outcome.Kind is not null).ToArray();
            foreach (var outcome in outcomes.GroupBy(item => item.Kind!))
            {
                FindingRoundBatchAccounting.Record(goal, identity,
                    new(identity.Key, outcome.Key, member.Identity, outcome.Select(item => item.FindingStableId).ToArray(), receipt.ReceiptId),
                    driver._recordFindingEvidenceRequest);
            }

            if (!validatedReplayCoverage) continue;
            // The scheduler can validate an earlier RED for the remaining selections
            // even when its disposition belongs to a standalone request.
            foreach (var finding in member.Findings.Where(finding =>
                         !outcomes.Any(outcome => outcome.FindingStableId == finding.StableId)))
                RecordCoverage(driver, goal, task, candidate, round, basis, member.Identity, finding.StableId, receipt.ReceiptId);
        }
    }

    internal static void RecordExecutedOutcomes(ConductorDriver driver, Goal goal, TaskSpec task, string candidate,
        string round, string basis, FindingEvidenceBatch batch, FindingEvidenceReceipt receipt, bool partial = false)
    {
        foreach (var member in batch.Members)
        foreach (var finding in member.Findings)
            RecordCurrentExecutedReceipt(driver, goal, task, candidate, round, basis, member.Identity, finding.StableId,
                receipt, partial);
    }

    internal static void RecordCurrentExecutedReceipt(ConductorDriver driver, Goal goal, TaskSpec task, string candidate,
        string round, string basis, string requestIdentity, string findingId, FindingEvidenceReceipt receipt, bool partial = false)
    {
        var disposition = FindingEvidenceRequestCompletion.FindSettledMember(receipt, requestIdentity, findingId);
        if (partial && disposition is null) return;
        var kind = disposition?.Decision == FindingEvidenceRequestDecision.CoveredByReceipt ? "covered" : "executed";
        var identity = new FindingEvidenceBatchIdentity(task.Id, candidate, round, basis);
        lock (goal)
        {
            // A fresh execution may already have settled this finding in a grouped observation.
            if (FindingRoundBatchAccounting.Read(goal, identity).Any(item => item.Kind == kind &&
                    item.ReceiptId == receipt.ReceiptId && item.FindingIds.Contains(findingId, StringComparer.Ordinal)))
                return;

            FindingRoundBatchAccounting.Record(goal, identity,
                new(identity.Key, kind, requestIdentity + ":" + findingId, [findingId], receipt.ReceiptId), driver._recordFindingEvidenceRequest);
        }
    }

    internal static void RecordCoverage(ConductorDriver driver, Goal goal, TaskSpec task, string candidate,
        string round, string basis, string requestIdentity, string findingId, string receiptId)
    {
        var identity = new FindingEvidenceBatchIdentity(task.Id, candidate, round, basis);
        FindingRoundBatchAccounting.Record(goal, identity,
            new(identity.Key, "covered", requestIdentity + ":" + findingId, [findingId], receiptId), driver._recordFindingEvidenceRequest);
    }

    internal static FindingEvidenceBatchIdentity? Identity(ConductorDriver driver, Goal goal, TaskSpec task) =>
        Identity(driver, goal, task, out _);

    private static FindingEvidenceBatchIdentity? Identity(ConductorDriver driver, Goal goal, TaskSpec task,
        out ExecutionContext? execution, bool useRetainedVerification = false)
    {
        execution = null;
        var verification = task.LastVerification ?? (useRetainedVerification ? task.VerificationHistory.LastOrDefault() : null);
        if (!WorkerResultBlockers.TryFindReviewFindingRound(verification, out var round, out _)) return null;
        execution = ResolveExecution(driver, goal, round);
        if (driver._getPreReviewEvidenceContext(goal).CandidateSha?.Trim() is not { } candidate ||
            !ConductorGitRevisionReader.IsValid(candidate)) return null;
        return new(task.Id, candidate, ConductorDriver.BuildFindingRoundFingerprint(task.Id, verification!.CompletedAt, round),
            execution.BatchBasis);
    }

    internal static bool TryAccountCoveredContinuation(ConductorDriver driver, Goal goal, TaskSpec task,
        FindingEvidenceReviewContinuation continuation, bool useRetainedVerification, out FindingEvidenceBatchClosure? closure)
    {
        closure = null;
        var identity = Identity(driver, goal, task, out var execution, useRetainedVerification);
        if (identity is null || !string.Equals(identity.CandidateSha, continuation.CandidateSha, StringComparison.OrdinalIgnoreCase) ||
            FindingRoundBatchAccounting.IsClosed(goal, identity) ||
            (!useRetainedVerification && FindingRoundBatchAccounting.HasOpenBatch(goal, identity))) return false;
        var findings = execution!.Round.Findings.Where(finding => finding.State == ReviewFindingState.Open).ToArray();
        var selected = continuation.Selections.Select(selection => selection.FindingId).ToHashSet(StringComparer.Ordinal);
        var members = FindingRoundBatchAccounting.Members(findings);
        if (members.Length == 0 || members.Any(member => !member.FindingIds.Any(selected.Contains))) return false;

        FindingRoundBatchAccounting.Begin(goal, identity, findings, driver._recordFindingEvidenceRequest);
        foreach (var selection in continuation.Selections.DistinctBy(selection => selection.FindingId))
            RecordCoverage(driver, goal, task, identity.CandidateSha, identity.RoundFingerprint,
                identity.ExecutionBasisIdentity, selection.RequestIdentity, selection.FindingId, selection.ReceiptId);
        var summary = FindingRoundBatchAccounting.Summarize(goal, task, identity, $"Reviewer: {continuation.Render()}");
        if (!summary.IsSettled || summary.Unavailable > 0) return false;
        closure = new(identity, JsonSerializer.Serialize(summary));
        return true;
    }

    internal static bool TrySettleBeforeRouting(ConductorDriver driver, Goal goal, TaskSpec task, string candidate,
        Func<DateTimeOffset> utcNow, out FindingReceiptClosureDiagnosis? diagnosis, out FailedGoalFindingObservation observation)
    {
        diagnosis = null;
        observation = FailedGoalFindingObservation.None;
        var identity = Identity(driver, goal, task);
        bool AlreadyClosed() => identity is not null && FindingRoundBatchAccounting.IsClosed(goal, identity);
        var summary = identity is not null && FindingRoundBatchAccounting.HasOpenBatch(goal, identity)
            ? FindingRoundBatchAccounting.Summarize(goal, task, identity, "deterministic-receipt-closure: Tester completed; no worker dispatched")
            : null;
        if (AlreadyClosed())
        {
            observation = FailedGoalFindingObservation.Observed(FailedGoalFindingObservationKind.FindingEvidencePending,
                "Finding batch closure was already applied; waiting for the routed owner.");
            return true;
        }
        if (summary is not null)
        {
            if (!summary.IsSettled)
            {
                observation = FailedGoalFindingObservation.Observed(FailedGoalFindingObservationKind.FindingEvidencePending,
                    "Distinct requests in the finding batch remain unsettled; downstream routing is deferred.");
                return true;
            }
            if (TryObserveRequiredUnavailable(goal, task, identity!, out observation))
            {
                return true;
            }
        }
        if (task.RequiredRole != AgentRole.Tester || (driver._cohortKernel ?? driver._conductorTickKernel) is not { } kernel) return false;
        FindingReceiptClosureDiagnosis? localDiagnosis = null;
        var localObservation = FailedGoalFindingObservation.None;
        bool Close() => FindingReceiptClosureCoordinator.TryClose(kernel, goal, task.Id, candidate, utcNow,
            out localDiagnosis, out localObservation);
        var closed = summary is null ? Close() : TryApply(driver, goal,
            new(identity!, JsonSerializer.Serialize(summary)), () => true, Close);
        diagnosis = localDiagnosis;
        if (closed) observation = localObservation;
        else if (AlreadyClosed())
            observation = FailedGoalFindingObservation.Observed(FailedGoalFindingObservationKind.FindingEvidencePending,
                "Finding batch closure was already applied; waiting for the routed owner.");
        return closed || AlreadyClosed();
    }

    internal static bool Observe(ConductorDriver driver, Goal goal, TaskSpec task, ConductorAutonomyPolicy policy,
        out FailedGoalFindingObservation decision)
    {
        var identity = Identity(driver, goal, task, out var execution);
        if (identity is not null && FindingRoundBatchAccounting.IsClosed(goal, identity))
        {
            decision = FailedGoalFindingObservation.Observed(FailedGoalFindingObservationKind.FindingEvidencePending,
                "Finding batch closure was already applied; waiting for the routed owner.");
            return true;
        }
        if (execution is null)
        {
            decision = FailedGoalFindingObservation.None;
            return false;
        }
        var findings = task.LastVerification?.MergedReviewFindings ?? [];
        if (identity is not null)
            FindingRoundBatchAccounting.Begin(goal, identity,
                execution.Round.Findings.Where(finding => finding.State == ReviewFindingState.Open),
                driver._recordFindingEvidenceRequest);
        void RecordPendingSuppressions(FindingRoundRedCoverage.Delivery? delivery, FailedGoalFindingObservation? retainedRed)
        {
            if (identity is not null && !FindingRoundBatchAccounting.IsClosed(goal, identity) &&
                !FindingRoundBatchAccounting.Summarize(goal, task, identity, "suppression-check").IsSettled)
                RecordSuppressionWitnesses(driver, goal, task, identity, execution, policy, findings, delivery, retainedRed);
        }

        FindingRoundRedCoverage.Delivery? completedDelivery = null;
        var handled = driver.TryBuildFindingEvidenceRequestCore(goal, task, policy, execution,
            delivery => completedDelivery = delivery, RecordPendingSuppressions, out var reusedRedDecision, out decision);
        if (identity is null) return handled;
        // Deterministic receipt closure records its summary atomically with the kernel action.
        if (FindingRoundBatchAccounting.IsClosed(goal, identity))
        {
            decision = FailedGoalFindingObservation.Observed(FailedGoalFindingObservationKind.FindingEvidencePending,
                "Finding batch closure was already applied; waiting for the routed owner.");
            return true;
        }
        var deferredSummary = FindingRoundBatchAccounting.Summarize(goal, task, identity, "deferred-routing-check");
        // Deferred requests require the existing review retry route, including its caps and owner selection.
        // They settle current-candidate membership without claiming evidence coverage or unavailability.
        if (deferredSummary is { IsSettled: true, Deferred: > 0 } &&
            FindingRoundBatchAccounting.RequiredUnavailable(goal, task, identity).Length == 0 &&
            decision.Kind != FailedGoalFindingObservationKind.FindingActionableRed &&
            (!handled || decision.TargetTaskId is not null)) return false;
        var settlement = FindingRoundBatchAccounting.Summarize(goal, task, identity, "settlement-check");
        if ((!handled || (settlement.IsSettled && settlement.Unavailable > 0 &&
            decision.Kind == FailedGoalFindingObservationKind.FindingOperatorEvidenceRequired)) &&
            settlement.BatchSize > 0)
        {
            if (settlement.IsSettled)
            {
                var receiptIds = task.VerificationHistory.SelectMany(item => item.FindingEvidenceReceipts ?? [])
                    .Where(receipt => execution.Matches(receipt, identity))
                    .Select(receipt => receipt.ReceiptId).Distinct().ToArray();
                if (!TryObserveRequiredUnavailable(goal, task, identity, out decision))
                    decision = driver.BuildReceiptClosureOrDeliveryRetry(goal, task, identity.CandidateSha, findings, receiptIds,
                        "All distinct requests in the finding batch have settled.", policy);
                handled = true;
            }
            else
            {
                decision = FailedGoalFindingObservation.Observed(FailedGoalFindingObservationKind.FindingEvidencePending,
                    "Distinct requests in the finding batch remain unsettled; downstream routing is deferred.");
                handled = true;
            }
        }
        if (settlement.IsSettled &&
            (decision.Kind == FailedGoalFindingObservationKind.FindingEvidencePending ||
                decision is { EvidenceContinuation: not null }) &&
            ReviewerPassingFindingEvidenceRouter.TryObserveContinuationCap(goal, task, policy, out var cap))
        {
            decision = cap;
            handled = true;
        }
        if (!handled) return false;

        if (!settlement.IsSettled)
        {
            if (decision.TargetTaskId is not null || decision.Kind == FailedGoalFindingObservationKind.FindingOperatorEvidenceRequired)
                decision = FailedGoalFindingObservation.Observed(FailedGoalFindingObservationKind.FindingEvidencePending,
                    "Distinct requests in the finding batch remain unsettled; downstream routing is deferred.");
            // Persistence and other unsafe failures keep their existing loud recovery path,
            // but cannot claim that an unsettled batch has closed.
            else if (decision.Kind != FailedGoalFindingObservationKind.FindingEvidencePending) return handled;
        }

        if (!settlement.IsSettled && decision.Kind == FailedGoalFindingObservationKind.FindingEvidencePending)
            RecordPendingSuppressions(completedDelivery, reusedRedDecision);
        else if (decision.Kind != FailedGoalFindingObservationKind.FindingEvidencePending)
        {
            var settled = FindingRoundBatchAccounting.Summarize(goal, task, identity, "settlement-check");
            if (settled.IsSettled && TryObserveRequiredUnavailable(goal, task, identity, out var unavailable))
                decision = unavailable;
            var route = decision.TargetTaskId is { } target
                ? goal.Tasks.Single(candidate => candidate.Id == target).RequiredRole.ToString()
                : decision.Kind.ToString();
            var summary = FindingRoundBatchAccounting.Summarize(goal, task, identity, route + ": " + decision.Evidence);
            if (summary.BatchSize > 0)
                decision = decision with { BatchClosure = new(identity, JsonSerializer.Serialize(summary)) };
        }
        return handled;
    }

    private static void RecordSuppressionWitnesses(ConductorDriver driver, Goal goal, TaskSpec task,
        FindingEvidenceBatchIdentity identity, ExecutionContext execution, ConductorAutonomyPolicy policy,
        IReadOnlyList<ReviewFinding> findings, FindingRoundRedCoverage.Delivery? completedDelivery,
        FailedGoalFindingObservation? reusedRedDecision)
    {
        if (TryObserveQualifiedReviewerSuppression(driver, goal, task, identity, policy)) return;

        // A passing receipt plus the production interpretation route is an eligibility witness.
        // Cases needing closure, continuation or policy information we did not evaluate stay unknown.
        var witnessed = FindingRoundBatchAccounting.Read(goal, identity)
            .Where(item => item.Kind is "suppressed-selection" or "ineligible-selection")
            .Select(item => item.ReceiptId).ToHashSet(StringComparer.Ordinal);
        var receipts = task.VerificationHistory.SelectMany(item => item.FindingEvidenceReceipts ?? [])
            .Where(receipt => !witnessed.Contains(receipt.ReceiptId) && execution.Matches(receipt, identity))
            .DistinctBy(receipt => receipt.ReceiptId);
        foreach (var receipt in receipts)
        {
            // These conditions prove ineligibility; they are zero, not unknown savings.
            if (task.Status != WorkTaskStatus.Failed || goal.Tasks.Any(candidate => candidate.LastProcess is { IsRunning: true }) ||
                Mcg.AgentOrchestrator.App.Application.DispatchReadinessRules.BuildIncompleteEarlierStageDependencies(goal, task).Any()) continue;
            // A retryable failure keeps its own request pending, independent of any siblings.
            if (receipt is { Passed: false, Arms: { } arms } &&
                arms.Any(arm => arm.Disposition is FindingEvidenceArmDisposition.ApparatusFailure or FindingEvidenceArmDisposition.Inconclusive) &&
                !arms.Any(arm => arm is { Arm: FindingEvidenceArm.Candidate, Disposition: FindingEvidenceArmDisposition.Red })) continue;
            // An incomplete GREEN execution cannot be delivered for interpretation.
            if (receipt is { Accepted: true, Passed: true } && (receipt.RequestDispositions ?? []).Any(disposition =>
                    FindingEvidenceRequestCompletion.IsIncompleteExecution(receipt, disposition))) continue;

            if (reusedRedDecision is not null && receipt is { Accepted: true, Passed: true })
            {
                if (reusedRedDecision.TargetTaskId is { } redTarget && PolicySelects(driver, goal, policy, reusedRedDecision))
                {
                    FindingRoundBatchAccounting.Record(goal, identity,
                        new(identity.Key, "suppressed-selection", receipt.ReceiptId + ":" + redTarget.Value + ":Developer",
                            [], receipt.ReceiptId, AgentRole.Developer, "recovery policy selects Developer through the retained actionable RED route"),
                        driver._recordFindingEvidenceRequest);
                }
                continue;
            }
            var open = ReviewFindings.GetOpenBlockingFindings(findings, goal.EffectiveAcceptanceCriteriaCorrections);
            var sourceRepair = TrySourceRepair(goal, task, identity.CandidateSha, [receipt.ReceiptId], open, out var sourceRoute);
            var diagnosis = FindingReceiptClosureDiagnosis.Evaluate(goal, task, identity.CandidateSha);
            var interpretation = PassingFindingEvidenceRouter.BuildDecision(goal, task, ConductorDriver.FindUpstreamDeveloperTask(goal, task),
                identity.CandidateSha, [receipt.ReceiptId], open, diagnosis, ConductorDriver.BuildFailedGoalAttemptIdentity);
            var canInterpret = !sourceRepair && task.RequiredRole == AgentRole.Tester && open.Count > 0 && !diagnosis.IsClosable &&
                FindingEvidenceReceiptSelector.SuppliedReceiptsPassAtCandidate(task, identity.CandidateSha, [receipt.ReceiptId]) &&
                interpretation.TargetTaskId == task.Id && PolicySelects(driver, goal, policy, interpretation);
            var developer = ConductorDriver.FindUpstreamDeveloperTask(goal, task);
            var canRepair = task.RequiredRole == AgentRole.Tester && task.Status == WorkTaskStatus.Failed &&
                developer?.Status == WorkTaskStatus.Completed && !goal.Tasks.Any(candidate => candidate.LastProcess is { IsRunning: true }) &&
                (sourceRepair || (receipt is { Accepted: true, Passed: false } && receipt.Arms?.Any(arm =>
                    arm is { Arm: FindingEvidenceArm.Candidate, Accepted: true, Disposition: FindingEvidenceArmDisposition.Red, ExecutedTestCount: > 0 }) == true)) &&
                open.Any(finding => finding.Category is FindingCategory.Correctness or FindingCategory.TestCoverage or
                    FindingCategory.SpecCompliance or FindingCategory.CodeQuality) &&
                PolicySelects(driver, goal, policy, sourceRepair ? sourceRoute : FailedGoalFindingObservation.Routed(
                    FailedGoalFindingObservationKind.FindingRouteObserved, developer!.Id,
                    ConductorDriver.BuildFailedGoalAttemptIdentity(developer), "Open source finding after evidence receipt.", null,
                    observedCause: RetryCause.NewSourceFinding));
            AgentRole? reviewerRouteRole = null;
            if (task.RequiredRole == AgentRole.Reviewer &&
                !ConductorDriver.UsesQualifiedReviewerContinuation(goal, task) &&
                receipt is { Accepted: true, Passed: true } &&
                completedDelivery is { } delivery && delivery.CompletedReceiptId == receipt.ReceiptId)
            {
                var reviewerRoute = delivery.ReviewerRoute ?? ConductorDriver.BuildCappedFindingEvidenceDeliveryRetry(
                    goal, task, identity.CandidateSha, delivery.Findings, delivery.ReceiptIds, "Focused evidence completed.");
                if (reviewerRoute.TargetTaskId is { } reviewerRouteTarget && PolicySelects(driver, goal, policy, reviewerRoute))
                    reviewerRouteRole = goal.Tasks.Single(candidate => candidate.Id == reviewerRouteTarget).RequiredRole;
                else if (reviewerRoute.Kind == FailedGoalFindingObservationKind.FindingRetryCapReached)
                {
                    FindingRoundBatchAccounting.Record(goal, identity,
                        new(identity.Key, "ineligible-selection", receipt.ReceiptId, [], receipt.ReceiptId,
                            AgentRole.Reviewer, "Reviewer evidence delivery retry cap reached"),
                        driver._recordFindingEvidenceRequest);
                    continue;
                }
                else if (delivery.ReviewerRoute is not null)
                    continue;
            }
            var role = canInterpret ? AgentRole.Tester : canRepair ? AgentRole.Developer :
                reviewerRouteRole;
            var target = reviewerRouteRole is not null && completedDelivery?.ReviewerRoute?.TargetTaskId is { } reviewerTarget
                ? reviewerTarget : canRepair ? developer!.Id : task.Id;
            FindingRoundBatchAccounting.Record(goal, identity,
                new(identity.Key, "suppressed-selection", receipt.ReceiptId + ":" + target.Value + ":" + (role?.ToString() ?? "unknown"),
                    [], receipt.ReceiptId, role,
                    canInterpret ? "passing-receipt; failed Tester; dependencies complete; interpretation route selects requesting Tester"
                        : canRepair ? "source-repair route; open Developer-owned source finding; completed upstream Developer; no running processes"
                        : reviewerRouteRole is not null ? $"passing-receipt; recovery policy selects {reviewerRouteRole} through the Reviewer evidence route"
                        : "unknown: no demonstrated eligible paid verification selection"), driver._recordFindingEvidenceRequest);
        }
    }

    private static bool TryObserveQualifiedReviewerSuppression(ConductorDriver driver, Goal goal, TaskSpec reviewer,
        FindingEvidenceBatchIdentity identity, ConductorAutonomyPolicy policy)
    {
        if (!ConductorDriver.UsesQualifiedReviewerContinuation(goal, reviewer) ||
            (driver._cohortKernel ?? driver._conductorTickKernel) is not { } kernel ||
            reviewer.Status != WorkTaskStatus.Failed ||
            goal.Tasks.Any(task => task.LastProcess is { IsRunning: true }) ||
            Mcg.AgentOrchestrator.App.Application.DispatchReadinessRules.BuildIncompleteEarlierStageDependencies(goal, reviewer).Any() ||
            !FindingEvidenceContinuationCoordinator.TrySelectFindingEvidenceContinuation(driver, goal, kernel.GetTask(goal.Id, reviewer.Id),
                out var selected, out _)) return false;

        var continuation = selected!;
        var route = FindingEvidenceContinuationCoordinator.BuildObservation(reviewer, continuation);
        if (!ReviewerPassingFindingEvidenceRouter.TryObserveContinuationCap(goal, reviewer, policy, out _) &&
            PolicySelects(driver, goal, policy, route))
        {
            var receipt = continuation.Selections.First(selection =>
                !continuation.PriorExecutionIdentities.Contains(selection.ExecutionIdentity, StringComparer.Ordinal));
            FindingRoundBatchAccounting.Record(goal, identity,
                new(identity.Key, "suppressed-selection", receipt.ReceiptId + ":" + continuation.Key, [], receipt.ReceiptId,
                    AgentRole.Reviewer, "qualified continuation; dependencies complete; recovery policy selects requesting Reviewer"),
                driver._recordFindingEvidenceRequest);
        }
        return true;
    }

    private static bool TryObserveRequiredUnavailable(Goal goal, TaskSpec task, FindingEvidenceBatchIdentity identity,
        out FailedGoalFindingObservation observation)
    {
        var unavailable = FindingRoundBatchAccounting.RequiredUnavailable(goal, task, identity);
        observation = FailedGoalFindingObservation.None;
        if (unavailable.Length == 0) return false;

        var required = FindingRoundBatchAccounting.RequiredFindingIds(goal, task);
        required.IntersectWith(unavailable.SelectMany(item => item.FindingIds));
        var attemptBackedFindings = unavailable.Where(item => item.ReceiptId is not null)
            .SelectMany(item => item.FindingIds).ToHashSet(StringComparer.Ordinal);
        if (TryBuildFailedExecutionDelivery(goal, task, identity.CandidateSha, required, out observation, attemptBackedFindings))
            return true;

        var detail = unavailable.FirstOrDefault(item => item.Eligibility is not null)?.Eligibility;
        observation = FailedGoalFindingObservation.Observed(FailedGoalFindingObservationKind.FindingOperatorEvidenceRequired,
            "Focused evidence batch settled with unavailable required evidence. No worker was dispatched. " + detail);
        return true;
    }

    private static bool TryBuildFailedExecutionDelivery(Goal goal, TaskSpec task, string candidate, IReadOnlySet<string> findingIds,
        out FailedGoalFindingObservation observation, IReadOnlySet<string>? attemptBackedFindings = null)
    {
        observation = FailedGoalFindingObservation.None;
        if (task.RequiredRole != AgentRole.Reviewer || ConductorDriver.UsesQualifiedReviewerContinuation(goal, task))
            return false;

        // A settlement can carry an attempt receipt ID even when the finding outcome has none.
        // Only a permanent refusal can take legacy delivery after an attempt-backed settlement.
        var findings = task.LastVerification?.MergedReviewFindings?.Where(finding => findingIds.Contains(finding.StableId)).ToArray() ?? [];
        if (findings.Length == 0 || findings.Length != findingIds.Count || findings.Any(finding =>
            finding.EvidenceOutcome is not { Honoured: false, ReceiptId: null, Reason:
                FindingEvidenceNotHonouredReason.UnsupportedProject or FindingEvidenceNotHonouredReason.UnparseableSelection or
                 FindingEvidenceNotHonouredReason.SelectionApparatusFailure or
                FindingEvidenceNotHonouredReason.RunFailed } outcome ||
            (attemptBackedFindings?.Contains(finding.StableId) == true && !FindingEvidenceExecutionClassifier.IsPermanentRefusal(outcome))))
            return false;

        observation = ConductorDriver.BuildCappedFindingEvidenceDeliveryRetry(goal, task, candidate, findings, [],
            "Focused evidence was unavailable for the affected findings; typed outcomes were attached.");
        return true;
    }

    internal static void RecordDeferred(ConductorDriver driver,
        Action<GoalId, TaskId, IReadOnlyList<FindingEvidenceRequestDisposition>> recordDispositions, Goal goal, TaskSpec task,
        IReadOnlyList<ReviewFinding> findings, string? candidate, string round, string basis, string reason)
    {
        recordDispositions(goal.Id, task.Id,
            findings.Select(finding => new FindingEvidenceRequestDisposition(finding.StableId,
                ConductorDriver.BuildFindingEvidenceIdentity(finding.EvidenceRequest!), "deferred-next-candidate", reason,
                candidate is not null ? FindingEvidenceRequestDecision.DeferredToNextCandidate
                    : FindingEvidenceRequestDecision.CandidateUnavailable,
                candidate, round, ExecutionBasisIdentity: basis)).ToArray());
        var identity = new FindingEvidenceBatchIdentity(task.Id, candidate ?? "unavailable", round, basis);
        foreach (var member in FindingRoundBatchAccounting.Members(findings))
            FindingRoundBatchAccounting.Record(goal, identity,
                new(identity.Key, "deferred", member.RequestIdentity, member.FindingIds), driver._recordFindingEvidenceRequest);
    }

    internal static FailedGoalFindingObservation AttachDeferredClosure(ConductorDriver driver, Goal goal,
        TaskSpec task, FailedGoalFindingObservation decision)
    {
        var identity = Identity(driver, goal, task);
        if (identity is null || !FindingRoundBatchAccounting.HasOpenBatch(goal, identity)) return decision;
        var route = decision.TargetTaskId is { } target
            ? goal.Tasks.Single(candidate => candidate.Id == target).RequiredRole.ToString() : decision.Kind.ToString();
        var summary = FindingRoundBatchAccounting.Summarize(goal, task, identity, route + ": " + decision.Evidence);
        return summary is { IsSettled: true, Deferred: > 0 } &&
            FindingRoundBatchAccounting.RequiredUnavailable(goal, task, identity).Length == 0
            ? decision with { BatchClosure = new(identity, JsonSerializer.Serialize(summary)) }
            : decision;
    }
}
