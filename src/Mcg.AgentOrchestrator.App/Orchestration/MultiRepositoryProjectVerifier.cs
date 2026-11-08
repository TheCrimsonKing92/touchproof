using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed class MultiRepositoryProjectVerifier(OrchestratorProjectRegistry registry, IGoalAcceptanceVerifier verifier)
{
    internal static string ProjectHome(OrchestratorProjectRegistry registry, string key) =>
        OrchestratorDataRoot.FromDirectory(registry.DataRootDirectory).ProjectDirectory(key);

    internal static OrchestratorProject ResolveProject(OrchestratorProjectRegistry registry, Goal goal, string key)
    {
        var project = registry.GetRequiredProject(key);
        var root = goal.Repositories!.CheckoutRoots[key];
        if (!string.Equals(Path.GetFullPath(project.RootDirectory), Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(key + ": registered checkout differs from the goal declaration.");
        return project;
    }

    internal AcceptanceVerificationResult Run(Goal goal, string key, CancellationToken cancellationToken)
    {
        var project = ResolveProject(registry, goal, key);
        var root = goal.Repositories!.CheckoutRoots[key];
        if (!GoalRepositoryBindingResolver.TryResolve(new GoalRepositorySet([key]), goal.Id,
                new Dictionary<string, string> { [key] = root }, out var bindings, out var failure))
            throw new InvalidOperationException(failure);
        var worktree = bindings![key].WorktreePath;
        var home = ProjectHome(registry, key);
        // Resolve explicitly before invoking the verifier. Never substitute the home project's manifest.
        _ = GoalAcceptanceVerifier.AcceptanceManifest.Load(worktree, changedFiles: null, home);
        return AcceptanceExecutionRunner.RunAttempt(verifier, worktree, goal.Id,
            GoalAcceptanceEvidenceBundleBuilder.GetChangedFiles(worktree, project.IntegrationBranch),
            null, null, cancellationToken,
            new AcceptanceRunExecutionOptions(ProjectHomeDirectory: home, IntegrationBranch: project.IntegrationBranch));
    }
}
