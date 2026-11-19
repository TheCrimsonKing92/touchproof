using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Round-local scheduling facts, recomputed from durable receipts and completed-run events.
internal sealed record FindingRoundBatchPlan(
    IReadOnlyList<(FindingEvidenceBatch Batch, string ReceiptId)> Passed,
    IReadOnlyList<(FindingEvidenceBatch Batch, FindingEvidenceReceipt Receipt)> RedCovered,
    IReadOnlyList<FindingEvidenceBatch> Executable,
    IReadOnlyList<(FindingEvidenceBatch Batch, int Count, string ReceiptId)> Unavailable,
    IReadOnlyList<FindingRoundBatchRefusal> Refusals)
{
    internal (FindingEvidenceBatch Batch, int Count, string ReceiptId)? RequiredCap(Goal goal, TaskSpec task)
    {
        var required = FindingRoundBatchAccounting.RequiredFindingIds(goal, task);
        foreach (var unavailable in Unavailable)
            if (unavailable.Batch.Findings.Any(finding => required.Contains(finding.StableId))) return unavailable;
        return null;
    }
    internal ReviewFinding[] CoveredFindings => RedCovered.SelectMany(pair => pair.Batch.Findings)
        .DistinctBy(finding => finding.StableId).ToArray();

    internal string[] CoveredReceiptIds => RedCovered.Select(pair => pair.Receipt.ReceiptId)
        .Distinct(StringComparer.Ordinal).ToArray();

    internal string CoveredEvidence => string.Join("; ", RedCovered.Select(pair =>
        $"request_identity={pair.Batch.Identity}; receipt_id={pair.Receipt.ReceiptId}"));
}

internal sealed record FindingRoundBatchRefusal(
    FindingEvidenceBatch Batch, string AttemptId, string ReceiptId, FindingEvidenceNotHonouredReason Reason);
