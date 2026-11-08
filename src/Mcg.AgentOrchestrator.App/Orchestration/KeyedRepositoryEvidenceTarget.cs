using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// A complete non-home execution target for one candidate. Resolution never borrows home resources.
internal sealed record KeyedRepositoryEvidenceTarget(
    string Key,
    string WorktreePath,
    string ProjectHomeDirectory,
    string IntegrationBranch,
    AcceptanceGateEngineSettings EngineSettings,
    string CandidateHead)
{
    internal static (KeyedRepositoryEvidenceTarget? Target, string Failure) TryResolve(
        Goal goal, string key, OrchestratorProjectRegistry registry)
    {
        if (goal.Repositories is not { } declaration || !declaration.Repositories.Contains(key) ||
            FindingEvidenceRequestNormalization.IsHomeRepository(key))
            return (null, "A declared non-default repository key is required.");

        try
        {
            var canonicalKey = declaration.Keys.Single(declared => string.Equals(declared, key, StringComparison.OrdinalIgnoreCase));
            var project = MultiRepositoryProjectVerifier.ResolveProject(registry, goal, canonicalKey);
            var root = declaration.CheckoutRoots[canonicalKey];
            if (!GoalRepositoryBindingResolver.TryResolve(new GoalRepositorySet([canonicalKey]), goal.Id,
                    new Dictionary<string, string> { [canonicalKey] = root }, out var bindings, out var failure))
                return (null, failure);

            var worktree = bindings![canonicalKey].WorktreePath;
            var home = MultiRepositoryProjectVerifier.ProjectHome(registry, canonicalKey);
            _ = GoalAcceptanceVerifier.AcceptanceManifest.Load(worktree, changedFiles: null, home);
            return (new KeyedRepositoryEvidenceTarget(canonicalKey, worktree, home, project.IntegrationBranch,
                AcceptanceGateEngineSettings.Load(worktree, home), ConductorGitRevisionReader.ReadRequiredCommit(worktree, "HEAD")),
                string.Empty);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IOException or System.Text.Json.JsonException)
        {
            return (null, ex.Message);
        }
    }
}
