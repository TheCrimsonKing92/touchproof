namespace Mcg.AgentOrchestrator.App.Cli;

// Owns the flags excluded from answer text and repeated evidence arguments.
internal static class AttentionAnswerMetadata
{
    internal static IReadOnlyList<string> WithoutAnswerMetadataFlags(IReadOnlyList<string> parts)
    {
        var filtered = new List<string>();
        for (var index = 0; index < parts.Count; index++)
        {
            if (parts[index] is "--operator-actor" or "--actor-kind" or "--evidence" or
                "--precedent" or "--idempotency-key" or "--route" or "--cause" or "--amendment")
            {
                if (++index >= parts.Count)
                    throw new ArgumentException($"{parts[index - 1]} requires a value.");
                continue;
            }
            filtered.Add(parts[index]);
        }
        return filtered;
    }

    internal static IReadOnlyList<string> GetAnswerEvidence(IReadOnlyList<string> parts)
    {
        var evidence = new List<string>();
        for (var index = 0; index < parts.Count - 1; index++)
            if (parts[index].Equals("--evidence", StringComparison.OrdinalIgnoreCase))
                evidence.Add(parts[++index]);
        return evidence;
    }
}
