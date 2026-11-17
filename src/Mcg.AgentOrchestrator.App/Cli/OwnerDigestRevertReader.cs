using System.Globalization;
using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal sealed record OwnerDigestRevertData(
    IReadOnlyList<OwnerDigestRevertCommit> Reverts,
    IReadOnlyList<OwnerDigestLandingCommits> Landings);

internal static class OwnerDigestRevertReader
{
    internal static OwnerDigestRevertAttribution? ReadAttribution(
        OrchestratorWorkspace workspace, DateTimeOffset since, DateTimeOffset until,
        IReadOnlyList<OwnerDigestGoalInput> goals)
    {
        var data = Read(workspace.ExecutionDirectory, workspace.IntegrationBranch, since, until, goals);
        return data is null ? null : OwnerDigestRevertAttribution.Attribute(data.Reverts, data.Landings);
    }

    internal static OwnerDigestRevertData? Read(
        string executionDirectory, string branch, DateTimeOffset? since, DateTimeOffset? until,
        IReadOnlyList<OwnerDigestGoalInput> goals)
    {
        try
        {
            // Do not let git discover an unrelated repository above the workspace.
            var gitEntry = Path.Combine(executionDirectory, ".git");
            if (!Directory.Exists(gitEntry) && !File.Exists(gitEntry)) return null;
            var branchRef = branch.StartsWith("refs/", StringComparison.Ordinal) ? branch : "refs/heads/" + branch;
            var log = GitCli.Run(executionDirectory, "log", "--no-color", "-i", "-F",
                "--grep=This reverts commit", "--format=%H%x1f%ct%x1f%B%x1e", branchRef, "--");
            if (!log.Succeeded || log.DrainTimedOut) return null;

            var reverts = new List<OwnerDigestRevertCommit>();
            foreach (var entry in log.Output.Split('\u001e', StringSplitOptions.RemoveEmptyEntries))
            {
                if (string.IsNullOrWhiteSpace(entry)) continue;
                var fields = entry.TrimStart('\r', '\n').Split('\u001f', 3);
                if (fields.Length != 3 || !long.TryParse(fields[1], NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out var seconds)) return null;
                var at = DateTimeOffset.FromUnixTimeSeconds(seconds);
                // Filter committer timestamps after walking history, including skewed dates.
                if ((since is null || at >= since.Value) && (until is null || at < until.Value))
                    reverts.Add(new(fields[0], fields[2]));
            }

            var landings = new List<OwnerDigestLandingCommits>();
            if (reverts.Count == 0) return new(reverts, landings);
            foreach (var goal in goals.Where(g => g.LandedAt is { } at && (until is null || at < until.Value))
                         .OrderBy(g => g.LandedAt).ThenBy(g => g.GoalId, StringComparer.Ordinal))
            {
                if (goal.LandingSha is not { } sha || !Regex.IsMatch(sha, @"\A[0-9a-fA-F]{7,40}\z"))
                    continue;
                var introduced = GitCli.Run(executionDirectory, "rev-list", sha, "^" + sha + "^1", "--");
                landings.Add(new(goal.GoalId, introduced.Succeeded && !introduced.DrainTimedOut
                    ? introduced.Output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries) : []));
            }
            return new(reverts, landings);
        }
        catch (Exception) { return null; }
    }
}
