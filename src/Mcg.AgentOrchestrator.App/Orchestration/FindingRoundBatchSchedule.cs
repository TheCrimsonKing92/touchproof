using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class FindingRoundBatchSchedule
{
    internal const int MaximumCompletedRuns = 3;

    private static readonly (FindingEvidenceNotHonouredReason Reason, string Suffix)[] PermanentRunFailureReasons =
        Enum.GetValues<FindingEvidenceNotHonouredReason>()
            .Where(reason => FindingEvidenceExecutionClassifier.IsPermanentRefusal(new(false, Reason: reason)))
            .Select(reason => (reason, $" reason={FindingEvidenceNotHonouredReasonJsonConverter.ToWireValue(reason)};"))
            .ToArray();

    // A failed-attempt charge is staged under the goal lock until its checkpoint succeeds.
    internal static IReadOnlyList<ProgressEvent> ReadRunTimeline(Goal goal)
    {
        lock (goal) return goal.Timeline;
    }

    internal static FindingRoundBatchPlan PlanRequests(Goal goal, TaskSpec task,
        IReadOnlyList<FindingEvidenceRequestGroup> groups, string candidate, string round, string basis,
        bool usesKeyedRepository)
    {
        var batches = PreTesterCoverageComposition.BindExecutionIdentities(FindingEvidenceRoundPlanner.BuildFindingEvidenceBatches(groups));
        batches = batches.Select(batch => BindPartialGreenRemainder(batch, candidate, round, basis)).ToArray();
        // Execution provenance belongs to this repository; batch membership belongs to the home candidate.
        if (usesKeyedRepository)
            batches = batches.Select(batch => batch with { Identity = batch.Identity + ":" + basis }).ToArray();
        return Plan(batches, task.Id, task.VerificationHistory.SelectMany(record => record.FindingEvidenceReceipts ?? []).ToArray(),
            candidate, round, basis, ReadRunTimeline(goal));
    }

    private static FindingEvidenceBatch BindPartialGreenRemainder(FindingEvidenceBatch batch,
        string candidate, string round, string basis) =>
        PartialGreenReceiptIds(batch, batch.Members.SelectMany(member => member.ReusedSourceReceipts ?? []), candidate, round, basis).Any()
            // Preserve the original partial receipt as proof of the selections already executed.
            ? batch with { Identity = ConductorDriver.BuildFindingEvidenceIdentity(batch.TypedRequest) }
            : batch;

    internal static IEnumerable<string> PartialGreenReceiptIds(FindingEvidenceBatch batch,
        IEnumerable<FindingEvidenceReceipt> receipts, string candidate, string round, string basis) =>
        receipts.Where(receipt => receipt is { Accepted: true, Passed: true } &&
                string.Equals(receipt.CandidateSha, candidate, StringComparison.OrdinalIgnoreCase) &&
                receipt.FindingRoundFingerprint == round && receipt.ExecutionBasisIdentity == basis &&
                (receipt.RequestDispositions ?? []).Any(disposition =>
                    FindingEvidenceRequestCompletion.IsPartialGreenDisposition(receipt, disposition) &&
                    batch.Members.Any(member => member.Identity == disposition.RequestIdentity &&
                        member.Findings.Any(finding => finding.StableId == disposition.FindingStableId))))
            .Select(receipt => receipt.ReceiptId);

    internal static FindingRoundBatchPlan Plan(
        IReadOnlyList<FindingEvidenceBatch> batches,
        TaskId requestingTaskId,
        IReadOnlyList<FindingEvidenceReceipt> receipts,
        string candidateSha,
        string findingRoundFingerprint,
        string executionBasisIdentity,
        IReadOnlyList<ProgressEvent> timeline)
    {
        if (candidateSha == "unavailable") return new([], [], batches, [], []);

        // Completing a batch in this round does not require reattaching content-bound
        // evidence. Cross-round reuse keeps its stricter artifact and coverage checks.
        var currentReceipts = receipts
            .Where(receipt => string.Equals(receipt.CandidateSha, candidateSha, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(receipt.FindingRoundFingerprint, findingRoundFingerprint, StringComparison.Ordinal) &&
                string.Equals(receipt.ExecutionBasisIdentity, executionBasisIdentity, StringComparison.Ordinal))
            .GroupBy(receipt => receipt.ReceiptId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.Ordinal);
        var passed = new List<(FindingEvidenceBatch Batch, string ReceiptId)>();
        var covered = new List<(FindingEvidenceBatch Batch, FindingEvidenceReceipt Receipt)>();
        var executable = new List<FindingEvidenceBatch>();
        var unavailable = new List<(FindingEvidenceBatch Batch, int Count, string ReceiptId)>();
        var refusals = new List<FindingRoundBatchRefusal>();
        foreach (var batch in batches)
        {
            var receiptId = ConductorDriver.CreateFindingEvidenceReceiptId(
                candidateSha, findingRoundFingerprint, batch.Identity, executionBasisIdentity);
            // Pre-upgrade receipts and run events still count when they bind to this basis.
            var legacyId = ConductorDriver.CreateFindingEvidenceReceiptId(candidateSha, findingRoundFingerprint, batch.Identity);
            var current = currentReceipts.GetValueOrDefault(receiptId) ?? currentReceipts.GetValueOrDefault(legacyId);
            if (current is { Accepted: true, Passed: true } &&
                !(current.RequestDispositions ?? []).Any(disposition =>
                    disposition.Decision == FindingEvidenceRequestDecision.PendingExecution &&
                    batch.Members.Any(member => member.Identity == disposition.RequestIdentity &&
                        member.Findings.Any(finding => finding.StableId == disposition.FindingStableId))))
            {
                passed.Add((batch, current.ReceiptId));
                continue;
            }
            var covering = receipts.FirstOrDefault(receipt => CoversRed(
                batch, receiptId, legacyId, receipt, candidateSha, findingRoundFingerprint, executionBasisIdentity));
            if (covering is not null)
            {
                covered.Add((batch, covering));
                continue;
            }
            // Earlier partial runs spent the same allowance even though the remainder has a new receipt ID.
            var partialReceiptIds = PartialGreenReceiptIds(batch, currentReceipts.Values, candidateSha,
                findingRoundFingerprint, executionBasisIdentity).ToHashSet(StringComparer.Ordinal);
            var runMarkers = partialReceiptIds.Prepend(legacyId).Prepend(receiptId).Distinct()
                .Select(id => (ReceiptId: id, Marker: $"receipt_id={id};")).ToArray();
            var findingMarker = $"finding_id={batch.Findings[0].StableId};";
            var basisMarker = $"execution_basis={executionBasisIdentity};";
            var chargedAttempts = new HashSet<string>(StringComparer.Ordinal);
            var runs = timeline.Where(item => item.Kind == ProgressKind.FindingEvidenceRunRecorded &&
                item.TaskId == requestingTaskId)
                .Select(item => (Event: item, runMarkers.FirstOrDefault(marker =>
                    item.Message.Contains(marker.Marker, StringComparison.Ordinal)).ReceiptId))
                .Where(run => run.ReceiptId is not null &&
                    (run.Event.Message.Contains(basisMarker, StringComparison.Ordinal) ||
                     (!run.Event.Message.Contains("execution_basis=", StringComparison.Ordinal) &&
                      (run.ReceiptId == receiptId || !receipts.Any(receipt =>
                          receipt.ReceiptId == run.ReceiptId && receipt.ExecutionBasisIdentity != executionBasisIdentity)))))
                .Where(run =>
                {
                    var message = run.Event.Message;
                    if (FailureAttemptId(message) is not { } attemptId)
                        return message.Contains(findingMarker, StringComparison.Ordinal);
                    var partialReceipt = partialReceiptIds.Contains(run.ReceiptId!)
                        ? currentReceipts[run.ReceiptId!] : null;
                    return FailureChargedToBatch(message, batch, findingMarker, partialReceipt) && chargedAttempts.Add(attemptId);
                })
                .ToArray();
            // A run-charge checkpoint can survive a restart before the task records the refusal.
            if (ReadPermanentRefusal(batch, runs.Select(run => run.Event), receiptId, executionBasisIdentity) is { } refusal)
                refusals.Add(refusal);
            else if (runs.Length >= MaximumCompletedRuns)
                unavailable.Add((batch, runs.Length, runs[^1].ReceiptId!));
            else executable.Add(batch);
        }
        return new(passed, covered, executable, unavailable, refusals);
    }

    private static bool FailureChargedToBatch(string message, FindingEvidenceBatch batch, string findingMarker,
        FindingEvidenceReceipt? partialReceipt)
    {
        const string prefix = FindingRoundBatchCoordinator.FailedAttemptMembersPrefix;
        var start = message.IndexOf(prefix, StringComparison.Ordinal);
        if (start >= 0)
        {
            var end = message.LastIndexOf("; reason=", StringComparison.Ordinal);
            var members = JsonSerializer.Deserialize<FindingRoundBatchAccounting.Member[]>(message[(start + prefix.Length)..end])
                ?? throw new InvalidOperationException("Focused evidence failure checkpoint membership is null.");
            return members.Any(charged => batch.Members.Any(member => member.Identity == charged.RequestIdentity &&
                member.Findings.Any(finding => charged.FindingIds.Contains(finding.StableId, StringComparer.Ordinal))));
        }

        // Older charges named only the first finding. A matching partial receipt identifies
        // that earlier run's members even after the original first finding has settled.
        return message.Contains(findingMarker, StringComparison.Ordinal) ||
            (partialReceipt?.RequestDispositions ?? []).Any(disposition =>
                disposition.SourceReceiptIds?.Contains(partialReceipt!.ReceiptId, StringComparer.Ordinal) == true &&
                message.Contains($"finding_id={disposition.FindingStableId};", StringComparison.Ordinal));
    }

    private static FindingRoundBatchRefusal? ReadPermanentRefusal(
        FindingEvidenceBatch batch, IEnumerable<ProgressEvent> runs, string receiptId, string basis)
    {
        var marker = $"; receipt_id={receiptId}; execution_basis={basis};";
        foreach (var run in runs)
        {
            if (FailureAttemptId(run.Message) is not { } attemptId ||
                !run.Message.Contains(marker, StringComparison.Ordinal)) continue;
            foreach (var (reason, suffix) in PermanentRunFailureReasons)
                if (run.Message.EndsWith(suffix, StringComparison.Ordinal)) return new(batch, attemptId, receiptId, reason);
        }
        return null;
    }

    private static string? FailureAttemptId(string message)
    {
        const string prefix = FindingRoundBatchCoordinator.FailedAttemptRunPrefix;
        if (!message.StartsWith(prefix, StringComparison.Ordinal)) return null;
        var end = message.IndexOf(';', prefix.Length);
        return end > prefix.Length ? message[prefix.Length..end] : null;
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
