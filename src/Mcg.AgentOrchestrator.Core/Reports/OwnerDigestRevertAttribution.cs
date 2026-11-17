using System.Text.RegularExpressions;

namespace Mcg.AgentOrchestrator.Core;

public sealed record OwnerDigestRevertCommit(string Sha, string Message);

public sealed record OwnerDigestLandingCommits(string GoalId, IReadOnlyCollection<string> IntroducedShas);

public sealed record OwnerDigestRevertMatch(string RevertSha, string? RevertedSha, string? GoalId);

public sealed record OwnerDigestRevertAttribution(int Count, IReadOnlyList<OwnerDigestRevertMatch> Matches)
{
    private static readonly Regex RevertedCommitLine = new(
        @"^[ \t]*This reverts commit ([0-9a-f]{40})\b",
        RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static OwnerDigestRevertAttribution Attribute(
        IReadOnlyList<OwnerDigestRevertCommit> reverts,
        IReadOnlyList<OwnerDigestLandingCommits> landings)
    {
        ArgumentNullException.ThrowIfNull(reverts);
        ArgumentNullException.ThrowIfNull(landings);
        var introducedBy = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var landing in landings)
            foreach (var sha in landing.IntroducedShas)
                introducedBy.TryAdd(sha, landing.GoalId);

        var matches = reverts.Select(revert =>
        {
            var match = RevertedCommitLine.Match(revert.Message);
            var revertedSha = match.Success ? match.Groups[1].Value : null;
            var goalId = revertedSha is not null && introducedBy.TryGetValue(revertedSha, out var id) ? id : null;
            // A correction bundled into the same landing never escaped that landing.
            if (goalId is not null && introducedBy.TryGetValue(revert.Sha, out var revertGoalId) &&
                string.Equals(goalId, revertGoalId, StringComparison.OrdinalIgnoreCase))
                goalId = null;
            return new OwnerDigestRevertMatch(revert.Sha, revertedSha, goalId);
        }).ToArray();
        return new(reverts.Count, matches);
    }
}
