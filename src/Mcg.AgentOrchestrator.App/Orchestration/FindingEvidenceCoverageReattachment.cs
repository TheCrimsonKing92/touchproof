using Mcg.AgentOrchestrator.Core;
using static Mcg.AgentOrchestrator.App.Orchestration.ConductorDriver;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Owns attachment of qualified coverage receipts and their outcome/timeline lineage.
internal static class FindingEvidenceCoverageReattachment
{
    internal static void Reattach(
        Goal goal,
        TaskSpec requestingTask,
        ReviewFinding finding,
        IReadOnlyList<FindingEvidenceReceipt> receipts,
        string requestedIdentity,
        string coverageReason,
        Action<GoalId, TaskId, string, FindingEvidenceOutcome, FindingEvidenceReceipt?> recordOutcome,
        Action<GoalId, TaskId, string> recordRequest)
    {
        if (receipts.Count == 0)
        {
            throw new InvalidOperationException("Coverage reuse requires at least one authoritative source receipt.");
        }

        var sourceReceiptIds = receipts.Select(receipt => receipt.ReceiptId).ToArray();
        var primaryReceipt = receipts[^1];
        var outcome = new FindingEvidenceOutcome(
            Honoured: true,
            ReceiptId: primaryReceipt.ReceiptId,
            ResultReason: FindingEvidenceOutcomeReason.ValidEvidence,
            RequestedSelectionIdentity: requestedIdentity,
            DecisionReason: receipts.All(source => source.FindingRoundFingerprint == "pre-tester-deferred")
                ? "pre-tester-covered" : $"reused-covered-green:{coverageReason}",
            SourceReceiptIds: sourceReceiptIds);
        foreach (var receipt in receipts)
        {
            recordOutcome(
                goal.Id, requestingTask.Id, finding.StableId, outcome, receipt);
            var sourceFindingId = (receipt.RequestDispositions ?? [])
                .LastOrDefault(existing => existing.Disposition.StartsWith("executed-", StringComparison.Ordinal))
                ?.FindingStableId ?? "unavailable";
            recordRequest(
                goal.Id,
                requestingTask.Id,
                $"finding-evidence disposition=reused-covered-green; route=coverage; role={requestingTask.RequiredRole}; " +
                $"task_id={requestingTask.Id}; finding_id={finding.StableId}; requested_identity={requestedIdentity}; " +
                $"executed_identity={BuildFindingEvidenceIdentity(receipt.Request)}; source_receipt_id={receipt.ReceiptId}; " +
                $"source_finding_id={sourceFindingId}; coverage={coverageReason}; candidate_sha={receipt.CandidateSha}");
        }
    }
}
