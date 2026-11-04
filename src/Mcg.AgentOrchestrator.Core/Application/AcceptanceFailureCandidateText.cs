namespace Mcg.AgentOrchestrator.Core;

// Owns candidate identity formatting in acceptance-failure reports.
internal static class AcceptanceFailureCandidateText
{
    internal static string Format(AcceptanceFailureSummary failure)
    {
        if (string.IsNullOrWhiteSpace(failure.BranchHeadSha) && string.IsNullOrWhiteSpace(failure.MainHeadSha))
        {
            return string.Empty;
        }

        return $" for candidate branch={FormatShortSha(failure.BranchHeadSha)} main={FormatShortSha(failure.MainHeadSha)}";
    }

    private static string FormatShortSha(string? sha) =>
        string.IsNullOrWhiteSpace(sha)
            ? "unknown"
            : sha.Trim()[..Math.Min(12, sha.Trim().Length)];
}
