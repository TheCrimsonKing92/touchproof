namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class AcceptanceRetryOutputEvidence
{
    internal static void AppendBounded(
        ICollection<string> evidence,
        IReadOnlyList<string> outputEvidence,
        ref int remainingEvidenceEntries)
    {
        var entryLimit = Math.Min(outputEvidence.Count, remainingEvidenceEntries);
        var retainedCount = 0;
        for (; retainedCount < entryLimit; retainedCount++)
        {
            var entry = AcceptanceRetryEvidenceBudget.BoundLine(outputEvidence[retainedCount]);
            if (!AcceptanceRetryEvidenceBudget.FitsOutput(evidence, entry, outputEvidence.Count - retainedCount - 1))
                break;
            evidence.Add(entry);
        }

        remainingEvidenceEntries -= retainedCount;
        if (retainedCount < outputEvidence.Count)
        {
            evidence.Add($"{outputEvidence.Count - retainedCount} more acceptance evidence entries omitted.");
        }
    }
}
