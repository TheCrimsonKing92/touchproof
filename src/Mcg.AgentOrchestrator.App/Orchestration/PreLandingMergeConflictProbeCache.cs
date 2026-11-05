using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed class PreLandingMergeConflictProbeCache(int capacity = PreLandingMergeConflictProbeCache.Capacity)
{
    internal const int Capacity = 1024;
    internal sealed record Key(string Repository, GoalId GoalId, string Branch, string Main, string FrozenPaths);
    private readonly BoundedLruFactCache<Key, AdditiveConflictProbeResult> _facts = new(capacity);
    internal int Count => _facts.Count;

    internal AdditiveConflictProbeResult GetOrProbe(Func<Key?> readKey, Func<AdditiveConflictProbeResult> probe)
    {
        var key = readKey();
        return _facts.GetOrCompute(key, probe, result =>
            result.Outcome is AdditiveConflictProbeOutcome.Resolvable or AdditiveConflictProbeOutcome.Unresolvable &&
            key is not null && string.Equals(result.BranchRevision, key.Branch, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(result.MainRevision, key.Main, StringComparison.OrdinalIgnoreCase),
            readKey, ConductorTickStepLedger.CountMergeConflictCache);
    }

    internal static Key? ReadKey(string directory, GoalId goalId, IReadOnlyCollection<string> frozenPaths, string integrationBranch)
    {
        try
        {
            var worktree = GoalWorktrees.TryResolve(directory, goalId);
            // Unsupported metadata layouts bypass reuse; the probe retains its original fallback.
            if (worktree is null || !GitRevisionReader.TryReadCurrentBranch(directory, out var current)) return null;
            var branch = ConductorGitRevisionReader.ReadRequiredCommit(worktree, "HEAD^{commit}");
            var main = ConductorGitRevisionReader.ReadRequiredCommit(directory, $"{current ?? integrationBranch}^{{commit}}");
            if (!AcceptancePolicyChangeDecision.IsFullSha(branch) || !AcceptancePolicyChangeDecision.IsFullSha(main)) return null;
            return new(Path.GetFullPath(directory), goalId, branch, main,
                JsonSerializer.Serialize(frozenPaths.Select(path => path.Replace('\\', '/').ToLowerInvariant())
                    .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return null;
        }
    }
}
