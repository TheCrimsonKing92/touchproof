namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class AcceptanceRetryOutputEvidence
{
    internal static void AppendBounded(
        ICollection<string> evidence,
        IReadOnlyList<string> outputEvidence,
        ref int remainingEvidenceEntries)
    {
        var retainedCount = Math.Min(outputEvidence.Count, remainingEvidenceEntries);
        for (var index = 0; index < retainedCount; index++)
        {
            evidence.Add(outputEvidence[index]);
        }

        remainingEvidenceEntries -= retainedCount;
        if (retainedCount < outputEvidence.Count)
        {
            evidence.Add($"{outputEvidence.Count - retainedCount} more acceptance evidence entries omitted.");
        }
    }
}
