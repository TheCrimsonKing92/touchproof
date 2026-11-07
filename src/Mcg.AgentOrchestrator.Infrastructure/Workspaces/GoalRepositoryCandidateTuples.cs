using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

/// <summary>Computes all declared repository identities, returning no partial candidate tuple.</summary>
public static class GoalRepositoryCandidateTuples
{
    public static bool TryCompute(
        GoalRepositorySet repositories,
        IReadOnlyDictionary<string, (string WorktreePath, string IntegrationBranch)> bindings,
        out RepositoryCandidateTuple? tuple,
        out string failureReason,
        Func<string, IReadOnlyList<string>, string>? manifestIdentity = null,
        string? projectHomeDirectory = null,
        Func<string, string?>? projectHomeDirectoryForWorktree = null,
        bool allowUnchangedRepositories = false) =>
        TryCompute(repositories, bindings, (path, branch) =>
        {
            var projectHome = projectHomeDirectoryForWorktree is null ? projectHomeDirectory : projectHomeDirectoryForWorktree(path);
            var ok = GoalWorktrees.TryComputeCandidateIdentity(path, branch, out var identity,
                out var reason, manifestIdentity, projectHome);
            if (!ok && allowUnchangedRepositories && reason == "empty-change")
                return GoalRepositoryUnchangedCandidate.Compute(path, manifestIdentity, projectHome);
            return (ok, identity, reason);
        }, out tuple, out failureReason);

    internal static bool TryCompute(
        GoalRepositorySet repositories,
        IReadOnlyDictionary<string, (string WorktreePath, string IntegrationBranch)> bindings,
        Func<string, string, (bool Ok, CandidateIdentity? Identity, string Reason)> compute,
        out RepositoryCandidateTuple? tuple,
        out string failureReason)
    {
        ArgumentNullException.ThrowIfNull(repositories);
        ArgumentNullException.ThrowIfNull(bindings);
        ArgumentNullException.ThrowIfNull(compute);
        tuple = null;
        failureReason = string.Empty;

        // Snapshot with ordinal keys even when the supplied dictionary has a different comparer.
        var snapshot = new Dictionary<string, (string WorktreePath, string IntegrationBranch)>(bindings, StringComparer.Ordinal);
        foreach (var key in repositories.Keys)
        {
            if (snapshot.ContainsKey(key)) continue;
            failureReason = key + ":missing-binding";
            return false;
        }
        foreach (var key in snapshot.Keys.OrderBy(key => key, StringComparer.Ordinal))
        {
            if (repositories.Keys.Contains(key, StringComparer.Ordinal)) continue;
            failureReason = key + ":unexpected-binding";
            return false;
        }

        var entries = new List<KeyValuePair<string, CandidateIdentity>>();
        foreach (var key in repositories.Keys)
        {
            var binding = snapshot[key];
            var result = compute(binding.WorktreePath, binding.IntegrationBranch);
            if (!result.Ok)
            {
                failureReason = key + ":" + result.Reason;
                return false;
            }
            if (result.Identity is null)
                throw new InvalidOperationException("Successful repository computation must supply an identity.");
            entries.Add(new(key, result.Identity));
        }

        tuple = new RepositoryCandidateTuple(entries);
        return true;
    }
}
