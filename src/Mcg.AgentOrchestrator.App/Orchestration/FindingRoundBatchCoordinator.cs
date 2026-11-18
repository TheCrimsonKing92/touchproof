using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class FindingRoundBatchCoordinator
{
    internal sealed record ExecutionContext(
        AcceptanceGateEngineSettings HomeSettings, string HomeBasis, string BatchBasis,
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
        return new(settings, home, batch, targets);
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

    internal static bool TryApply(ConductorDriver driver, Goal goal, FindingEvidenceBatchClosure closure, Func<bool> isCurrent, Func<bool> apply)
    {
        bool Validate() => Identity(driver, goal, goal.Tasks.Single(task => task.Id == closure.Identity.RequestingTaskId)) == closure.Identity && isCurrent();
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

    internal static FailedGoalFindingObservation SettleUnavailable(ConductorDriver driver, Goal goal, TaskSpec task,
        FindingEvidenceBatch batch, bool morePending, string candidate, string round, string basis, string reason)
    {
        RecordOutcome(driver, goal, task, candidate, round, basis, batch, "unavailable", null, reason);
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

    internal static FailedGoalFindingObservation RecordExecutionFailure(ConductorDriver driver, Goal goal, TaskSpec task,
        FindingEvidenceBatch batch, bool morePending, string candidate, string round, string basis,
        FindingEvidenceNotHonouredReason reason, string detail, string batchCandidate, string batchBasis)
    {
        // Rejected and failed executions consume the same existing allowance as completed runs.
        var receiptId = ConductorDriver.CreateFindingEvidenceReceiptId(candidate, round, batch.Identity);
        driver._recordFindingEvidenceRun(goal.Id, task.Id,
            $"finding-evidence failed-attempt; finding_id={batch.Findings[0].StableId}; candidate_sha={candidate}; " +
            $"receipt_id={receiptId}; execution_basis={basis}; reason={FindingEvidenceNotHonouredReasonJsonConverter.ToWireValue(reason)};");
        return FindingEvidenceExecutionClassifier.IsPermanentRefusal(new(false, Reason: reason))
            ? SettleUnavailable(driver, goal, task, batch, morePending, batchCandidate, round, batchBasis, detail)
            : FailedGoalFindingObservation.Observed(FailedGoalFindingObservationKind.FindingEvidencePending,
                "Focused evidence execution failed transiently; the request remains pending within its run allowance. " + detail);
    }

    internal static void RecordSettledPlan(ConductorDriver driver, Goal goal, TaskSpec task, string candidate,
        string round, string basis, FindingRoundBatchPlan plan, string batchCandidate)
    {
        foreach (var unavailable in plan.Unavailable)
            RecordOutcome(driver, goal, task, batchCandidate, round, basis, unavailable.Batch, "unavailable", unavailable.ReceiptId,
                $"Request {unavailable.Batch.Identity} completed {unavailable.Count} runs; last_receipt_id={unavailable.ReceiptId}.");
        foreach (var passed in plan.Passed)
            RecordOutcome(driver, goal, task, batchCandidate, round, basis, passed, "executed",
                ConductorDriver.CreateFindingEvidenceReceiptId(candidate, round, passed.Identity));
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

    internal static void RecordCurrentReceipt(ConductorDriver driver, Goal goal, TaskSpec task, string candidate,
        string round, string basis, string requestIdentity, string findingId, string receiptId)
    {
        var identity = new FindingEvidenceBatchIdentity(task.Id, candidate, round, basis);
        FindingRoundBatchAccounting.Record(goal, identity,
            new(identity.Key, "executed", requestIdentity + ":" + findingId, [findingId], receiptId), driver._recordFindingEvidenceRequest);
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
        out ExecutionContext? execution)
    {
        execution = null;
        if (!WorkerResultBlockers.TryFindReviewFindingRound(task.LastVerification, out var round, out _)) return null;
        execution = ResolveExecution(driver, goal, round);
        if (driver._getPreReviewEvidenceContext(goal).CandidateSha?.Trim() is not { } candidate ||
            !ConductorGitRevisionReader.IsValid(candidate)) return null;
        return new(task.Id, candidate, ConductorDriver.BuildFindingRoundFingerprint(task, round), execution.BatchBasis);
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
        var findings = task.LastVerification?.MergedReviewFindings ?? [];
        if (identity is not null)
            FindingRoundBatchAccounting.Begin(goal, identity, findings.Where(finding => finding.State == ReviewFindingState.Open),
                driver._recordFindingEvidenceRequest);
        if (execution is null)
        {
            decision = FailedGoalFindingObservation.None;
            return false;
        }
        FindingRoundRedCoverage.Delivery? completedDelivery = null;
        var handled = driver.TryBuildFindingEvidenceRequestCore(goal, task, policy, execution,
            delivery => completedDelivery = delivery, out decision);
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
            FindingRoundBatchAccounting.RequiredUnavailable(goal, task, identity) is null &&
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
        {
            // A passing receipt plus the production interpretation route is an eligibility witness.
            // Cases needing closure, continuation or policy information we did not evaluate stay unknown.
            var witnessed = FindingRoundBatchAccounting.Read(goal, identity).Where(item => item.Kind == "suppressed-selection")
                .Select(item => item.ReceiptId).ToHashSet(StringComparer.Ordinal);
            var receipts = task.VerificationHistory.SelectMany(item => item.FindingEvidenceReceipts ?? [])
                .Where(receipt => !witnessed.Contains(receipt.ReceiptId) && execution.Matches(receipt, identity))
                .DistinctBy(receipt => receipt.ReceiptId);
            foreach (var receipt in receipts)
            {
                // These conditions prove ineligibility; they are zero, not unknown savings.
                if (task.Status != WorkTaskStatus.Failed || goal.Tasks.Any(candidate => candidate.LastProcess is { IsRunning: true }) ||
                    Mcg.AgentOrchestrator.App.Application.DispatchReadinessRules.BuildIncompleteEarlierStageDependencies(goal, task).Any()) continue;
                var open = ReviewFindings.GetOpenBlockingFindings(findings, goal.EffectiveAcceptanceCriteriaCorrections);
                var sourceRepair = TrySourceRepair(goal, task, identity.CandidateSha, [receipt.ReceiptId], open, out var sourceRoute);
                var interpretation = PassingFindingEvidenceRouter.BuildDecision(goal, task, ConductorDriver.FindUpstreamDeveloperTask(goal, task),
                    identity.CandidateSha, [receipt.ReceiptId], open,
                    FindingReceiptClosureDiagnosis.Evaluate(goal, task, identity.CandidateSha), ConductorDriver.BuildFailedGoalAttemptIdentity);
                var canInterpret = !sourceRepair && task.RequiredRole == AgentRole.Tester && task.Status == WorkTaskStatus.Failed &&
                    !goal.Tasks.Any(candidate => candidate.LastProcess is { IsRunning: true }) &&
                    !Mcg.AgentOrchestrator.App.Application.DispatchReadinessRules.BuildIncompleteEarlierStageDependencies(goal, task).Any() &&
                    open.Count > 0 && !FindingReceiptClosureDiagnosis.Evaluate(goal, task, identity.CandidateSha).IsClosable &&
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
                var canDeliverToReviewer = task.RequiredRole == AgentRole.Reviewer &&
                    !ConductorDriver.UsesQualifiedReviewerContinuation(goal, task) &&
                    receipt is { Accepted: true, Passed: true } &&
                    completedDelivery is { } delivery && delivery.CompletedReceiptId == receipt.ReceiptId &&
                    PolicySelects(driver, goal, policy, ConductorDriver.BuildCappedFindingEvidenceDeliveryRetry(
                        goal, task, identity.CandidateSha, delivery.Findings, delivery.ReceiptIds, "Focused evidence completed."));
                var role = canInterpret ? AgentRole.Tester : canRepair ? AgentRole.Developer :
                    canDeliverToReviewer ? AgentRole.Reviewer : (AgentRole?)null;
                var target = canRepair ? developer!.Id : task.Id;
                FindingRoundBatchAccounting.Record(goal, identity,
                    new(identity.Key, "suppressed-selection", receipt.ReceiptId + ":" + target.Value + ":" + (role?.ToString() ?? "unknown"),
                        [], receipt.ReceiptId, role,
                        canInterpret ? "passing-receipt; failed Tester; dependencies complete; interpretation route selects requesting Tester"
                            : canRepair ? "source-repair route; open Developer-owned source finding; completed upstream Developer; no running processes"
                            : canDeliverToReviewer ? "passing-receipt; capped evidence delivery selects requesting Reviewer"
                            : "unknown: no demonstrated eligible paid verification selection"), driver._recordFindingEvidenceRequest);
            }
        }
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

    private static bool TryObserveRequiredUnavailable(Goal goal, TaskSpec task, FindingEvidenceBatchIdentity identity,
        out FailedGoalFindingObservation observation)
    {
        var unavailable = FindingRoundBatchAccounting.RequiredUnavailable(goal, task, identity);
        observation = unavailable is null ? FailedGoalFindingObservation.None :
            FailedGoalFindingObservation.Observed(FailedGoalFindingObservationKind.FindingOperatorEvidenceRequired,
                "Focused evidence batch settled with unavailable required evidence. No worker was dispatched. " + unavailable.Eligibility);
        return unavailable is not null;
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
            FindingRoundBatchAccounting.RequiredUnavailable(goal, task, identity) is null
            ? decision with { BatchClosure = new(identity, JsonSerializer.Serialize(summary)) }
            : decision;
    }
}
