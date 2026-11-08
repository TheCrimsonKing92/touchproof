using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed class MultiRepositoryIntegrationBases(OrchestratorWorkspace workspace, OrchestratorProjectRegistry registry)
{
    internal RepositoryLandingTarget Resolve(string key)
    {
        var project = key == "default" ? null : registry.GetRequiredProject(key);
        var root = project?.RootDirectory ?? workspace.ExecutionDirectory;
        var branch = project?.IntegrationBranch ?? workspace.IntegrationBranch;
        return new(key, root, branch, RepositoryIntegrationLanding.Git(root, "rev-parse", "refs/heads/" + branch));
    }

    internal IReadOnlyDictionary<string, string> Capture(Goal goal) =>
        goal.Repositories!.Keys.ToDictionary(key => key, key => Resolve(key).Tip, StringComparer.Ordinal);
}
