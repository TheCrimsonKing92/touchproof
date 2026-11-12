namespace Mcg.AgentOrchestrator.Core;

/// <summary>One completed landing, preserving accepted identities separately from integration commits.</summary>
public sealed record MultiRepositoryLandingRecord(GoalId GoalId, string TupleCanonical,
    IReadOnlyDictionary<string, string> LandedCommits, IReadOnlyDictionary<string, string> PreLandingTips,
    IReadOnlyList<string> Order, DateTimeOffset RecordedAt)
{
    public bool MatchesLandedHomeCommit(string commit) =>
        LandedCommits.TryGetValue("default", out var landed) &&
        string.Equals(landed, commit, StringComparison.OrdinalIgnoreCase);
}
