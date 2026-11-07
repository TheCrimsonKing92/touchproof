using System.Security.Cryptography;
using System.Text;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class GoalRepositoryCandidateIdentity
{
    internal static (CandidateIdentity? Identity, string Failure) Compute(Goal goal, OrchestratorWorkspace workspace)
    {
        var declaration = goal.Repositories!;
        var home = GoalWorktrees.TryResolve(workspace.ExecutionDirectory, goal.Id);
        if (home is null) return (null, "default:missing-worktree");
        var roots = declaration.CheckoutRoots.Where(entry => entry.Key != "default")
            .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);
        if (!GoalRepositoryBindingResolver.TryResolve(new GoalRepositorySet(roots.Keys), goal.Id, roots,
                out var secondary, out var failure)) return (null, failure);
        var bindings = new Dictionary<string, (string WorktreePath, string IntegrationBranch)>(secondary!, StringComparer.Ordinal)
            { ["default"] = (home, workspace.IntegrationBranch) };
        if (!GoalRepositoryCandidateTuples.TryCompute(declaration.Repositories, bindings, out var tuple,
                out failure, projectHomeDirectoryForWorktree:
                    path => path == home ? workspace.ProjectHomeDirectoryOrNull : null,
                allowUnchangedRepositories: true)) return (null, failure);
        var homeIdentity = tuple!.Entries.Single(entry => entry.Key == "default").Value;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(tuple.Canonical))).ToLowerInvariant();
        return (new CandidateIdentity("set-v1-" + hash, homeIdentity.BaseClosureHash, homeIdentity.ManifestIdentity)
            { RepositoryTupleCanonical = tuple.Canonical }, string.Empty);
    }
}
