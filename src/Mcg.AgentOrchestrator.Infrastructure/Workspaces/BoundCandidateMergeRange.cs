namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class BoundCandidateMergeRange
{
    internal static string? ContainedBound(string directory, string boundCandidate, string evaluatedHead)
    {
        var ancestry = GitCli.Run(directory, "merge-base", "--is-ancestor", boundCandidate, evaluatedHead);
        return ancestry.ProcessStarted && !ancestry.DrainTimedOut && ancestry.ExitCode == 0
            ? boundCandidate : null;
    }

    internal static string[] ListArguments(string baseSha, string headSha, string? containedBound) =>
        containedBound is null
            ? ["rev-list", "--merges", $"{baseSha}..{headSha}"]
            // Keep main's own merges excluded, and retain the integration base for ratchet provenance.
            : ["rev-list", "--merges", $"{baseSha}..{headSha}", $"^{containedBound}"];
}
