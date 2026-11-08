using System.Security.Cryptography;
using System.Text;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class GoalRepositoryCandidateIdentity
{
    internal static (CandidateIdentity? Identity, string Failure) Compute(Goal goal, OrchestratorWorkspace workspace,
        OrchestratorProjectRegistry? registry = null)
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
        registry ??= OrchestratorProjectRegistry.CreateDefault();
        var projectHomes = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            { [home] = workspace.ProjectHomeDirectoryOrNull };
        foreach (var key in roots.Keys)
        {
            // Unknown projects still get a candidate when possible, then fail with keyed gate evidence.
            try
            {
                var project = registry.GetRequiredProject(key);
                bindings[key] = (bindings[key].WorktreePath, project.IntegrationBranch);
                projectHomes[bindings[key].WorktreePath] = MultiRepositoryProjectVerifier.ProjectHome(registry, key);
            }
            catch (ArgumentException) { }
        }
        if (!GoalRepositoryCandidateTuples.TryCompute(declaration.Repositories, bindings, out var tuple,
                out failure, projectHomeDirectoryForWorktree:
                    path => projectHomes.GetValueOrDefault(path),
                allowUnchangedRepositories: true)) return (null, failure);
        var homeIdentity = tuple!.Entries.Single(entry => entry.Key == "default").Value;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(tuple.Canonical))).ToLowerInvariant();
        return (new CandidateIdentity("set-v1-" + hash, homeIdentity.BaseClosureHash, homeIdentity.ManifestIdentity)
            { RepositoryTupleCanonical = tuple.Canonical }, string.Empty);
    }
}
