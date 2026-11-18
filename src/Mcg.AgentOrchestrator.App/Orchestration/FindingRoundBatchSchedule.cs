using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class FindingRoundBatchSchedule
{
    internal const int MaximumCompletedRuns = 3;

    internal static FindingRoundBatchPlan Plan(
        IReadOnlyList<FindingEvidenceBatch> batches,
        TaskId requestingTaskId,
        IReadOnlyList<FindingEvidenceReceipt> receipts,
        string candidateSha,
        string findingRoundFingerprint,
        string executionBasisIdentity,
        IReadOnlyList<ProgressEvent> timeline)
    {
        if (candidateSha == "unavailable") return new([], [], batches, []);

        // Completing a batch in this round does not require reattaching content-bound
        // evidence. Cross-round reuse keeps its stricter artifact and coverage checks.
        var passedReceiptIds = receipts
            .Where(receipt => receipt is { Accepted: true, Passed: true } &&
                string.Equals(receipt.CandidateSha, candidateSha, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(receipt.FindingRoundFingerprint, findingRoundFingerprint, StringComparison.Ordinal) &&
                string.Equals(receipt.ExecutionBasisIdentity, executionBasisIdentity, StringComparison.Ordinal))
            .Select(receipt => receipt.ReceiptId)
            .ToHashSet(StringComparer.Ordinal);
        var passed = new List<(FindingEvidenceBatch Batch, string ReceiptId)>();
        var covered = new List<(FindingEvidenceBatch Batch, FindingEvidenceReceipt Receipt)>();
        var executable = new List<FindingEvidenceBatch>();
        var unavailable = new List<(FindingEvidenceBatch Batch, int Count, string ReceiptId)>();
        foreach (var batch in batches)
        {
            var receiptId = ConductorDriver.CreateFindingEvidenceReceiptId(
                candidateSha, findingRoundFingerprint, batch.Identity, executionBasisIdentity);
            // Pre-upgrade receipts and run events still count when they bind to this basis.
            var legacyId = ConductorDriver.CreateFindingEvidenceReceiptId(candidateSha, findingRoundFingerprint, batch.Identity);
            if (passedReceiptIds.Contains(receiptId) || passedReceiptIds.Contains(legacyId))
            {
                passed.Add((batch, passedReceiptIds.Contains(receiptId) ? receiptId : legacyId));
                continue;
            }
            var covering = receipts.FirstOrDefault(receipt => CoversRed(
                batch, receiptId, legacyId, receipt, candidateSha, findingRoundFingerprint, executionBasisIdentity));
            if (covering is not null)
            {
                covered.Add((batch, covering));
                continue;
            }
            var marker = $"receipt_id={receiptId};";
            var legacyMarker = $"receipt_id={legacyId};";
            var runs = timeline.Where(item => item.Kind == ProgressKind.FindingEvidenceRunRecorded &&
                item.TaskId == requestingTaskId &&
                (item.Message.Contains(marker, StringComparison.Ordinal) ||
                 item.Message.Contains(legacyMarker, StringComparison.Ordinal)) &&
                item.Message.Contains($"finding_id={batch.Findings[0].StableId};", StringComparison.Ordinal) &&
                (item.Message.Contains($"execution_basis={executionBasisIdentity};", StringComparison.Ordinal) ||
                 (!item.Message.Contains("execution_basis=", StringComparison.Ordinal) &&
                  (item.Message.Contains(marker, StringComparison.Ordinal) ||
                   !receipts.Any(receipt => receipt.ReceiptId == legacyId && receipt.ExecutionBasisIdentity != executionBasisIdentity)))))
                .ToArray();
            if (runs.Length >= MaximumCompletedRuns)
            {
                var lastReceiptId = runs[^1].Message.Contains(marker, StringComparison.Ordinal) ? receiptId : legacyId;
                unavailable.Add((batch, runs.Length, lastReceiptId));
            }
            else executable.Add(batch);
        }
        return new(passed, covered, executable, unavailable);
    }

    private static bool CoversRed(
        FindingEvidenceBatch batch, string receiptId, string legacyReceiptId, FindingEvidenceReceipt receipt,
        string candidateSha, string findingRoundFingerprint, string executionBasisIdentity)
    {
        if (receipt is not { Accepted: true, Passed: false } ||
            !string.Equals(receipt.CandidateSha, candidateSha, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(receipt.FindingRoundFingerprint, findingRoundFingerprint, StringComparison.Ordinal) ||
            !string.Equals(receipt.ExecutionBasisIdentity, executionBasisIdentity, StringComparison.Ordinal) ||
            receipt.Request.NegativeControl != batch.TypedRequest.NegativeControl ||
            !FindingEvidenceRevertPaths.SamePaths(receipt.Request.RevertPaths, batch.TypedRequest.RevertPaths) ||
            !FindingEvidenceMutation.Same(receipt.Request.Mutation, batch.TypedRequest.Mutation)) return false;

        var arms = receipt.Arms ?? [];
        var candidate = arms.SingleOrDefault(arm => arm.Arm == FindingEvidenceArm.Candidate);
        if (candidate is not { Accepted: true, Disposition: FindingEvidenceArmDisposition.Red,
                ExecutedTestCount: > 0, FailingTestIdentities.Count: > 0 } ||
            !string.Equals(candidate.Sha, candidateSha, StringComparison.OrdinalIgnoreCase) ||
            arms.Any(arm => arm.Disposition is FindingEvidenceArmDisposition.ApparatusFailure or
                FindingEvidenceArmDisposition.Inconclusive) ||
            !batch.Findings.All(finding => (receipt.RequestDispositions ?? []).Any(disposition =>
                disposition.FindingStableId == finding.StableId &&
                disposition.Decision == FindingEvidenceRequestDecision.Executed))) return false;

        if (!FindingEvidenceRoundPlanner.HasClassSet(batch.TypedRequest))
            return receipt.ReceiptId == receiptId || receipt.ReceiptId == legacyReceiptId;
        var executed = FindingEvidenceExecutedCoverage.ExecutedSelections(receipt,
            selection => selection.TestProject + ":" + selection.TestClass).ToHashSet();
        return executed.IsSupersetOf(FindingEvidenceRoundPlanner.ExpandSelections(batch.TypedRequest.Selections));
    }

    internal static FailedGoalFindingObservation BuildRunCapDecision(
        Goal goal, TaskSpec requestingTask, FindingEvidenceBatch batch, int count, string receiptId,
        string candidateSha, string findingRoundFingerprint,
        Action<GoalId, TaskId, string> recordRequest)
    {
        var text = $"Focused evidence request {batch.Identity} completed {count} runs at candidate {candidateSha} " +
            $"in finding round {findingRoundFingerprint} without a Green receipt or a Developer route; " +
            $"runs={count}; last_receipt_id={receiptId}. No worker was dispatched.";
        var marker = $"finding-evidence run-cap-escalated; receipt_id={receiptId}; finding_id={batch.Findings[0].StableId};";
        // Keep the escalation one-shot across conductor restarts; later ticks still cannot dispatch.
        var recorded = goal.Timeline.Any(item => item.Kind == ProgressKind.FindingEvidenceRequestRecorded &&
            item.TaskId == requestingTask.Id && item.Message.StartsWith(marker, StringComparison.Ordinal));
        if (!recorded) recordRequest(goal.Id, requestingTask.Id, marker + " " + text);
        return FailedGoalFindingObservation.Observed(recorded
            ? FailedGoalFindingObservationKind.FindingEvidencePending
            : FailedGoalFindingObservationKind.FindingOperatorEvidenceRequired, text);
    }

    internal static void RecordSuperseded(
        Goal goal, TaskSpec requestingTask, IEnumerable<FindingEvidenceBatch> batches,
        string candidateSha, FindingEvidenceReceipt receipt, FindingEvidenceBatchIdentity batchIdentity,
        Action<GoalId, TaskId, string, FindingEvidenceOutcome, FindingEvidenceReceipt?> recordOutcome,
        Action<GoalId, TaskId, string> recordRequest)
    {
        var receiptId = receipt.ReceiptId;
        // Other repository partitions can still list requests settled earlier this tick.
        var settledFindingIds = FindingRoundBatchAccounting.Read(goal, batchIdentity)
            .Where(item => item.Kind is "executed" or "covered" or "unavailable" or "deferred" or "superseded")
            .SelectMany(item => item.FindingIds).ToHashSet(StringComparer.Ordinal);
        foreach (var pending in batches)
        foreach (var member in pending.Members)
        {
            var unsettledFindings = member.Findings.Where(finding => !settledFindingIds.Contains(finding.StableId)).ToArray();
            if (unsettledFindings.Length == 0) continue;

            FindingRoundBatchAccounting.Record(goal, batchIdentity,
                new(batchIdentity.Key, "superseded", member.Identity,
                    unsettledFindings.Select(item => item.StableId).ToArray(), receiptId), recordRequest);
            foreach (var finding in unsettledFindings)
            {
                recordOutcome(
                    goal.Id,
                    requestingTask.Id,
                    finding.StableId,
                    new FindingEvidenceOutcome(
                        Honoured: false,
                        ReceiptId: receiptId,
                        Reason: FindingEvidenceNotHonouredReason.SupersededByActionableRed,
                        Detail: $"Superseded by actionable candidate RED receipt {receiptId} at {candidateSha}.",
                        ResultReason: FindingEvidenceOutcomeReason.CandidateRed),
                    receipt);
                recordRequest(
                    goal.Id,
                    requestingTask.Id,
                    $"finding-evidence disposition=superseded; role={requestingTask.RequiredRole}; " +
                    $"task_id={requestingTask.Id}; finding_id={finding.StableId}; candidate_sha={candidateSha}; " +
                    $"receipt_id={receiptId}; reason=superseded-by-actionable-red; request_identity={member.Identity}");
            }
        }
    }
}
