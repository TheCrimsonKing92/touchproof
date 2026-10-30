using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

/// <summary>Resolves the worker worktree and sandbox mode before probing its tools.</summary>
internal static class WorkerEnvironmentProbeContexts
{
    internal static IReadOnlyList<EnvironmentProbeContext> Capture(OrchestratorWorkspace workspace, Goal goal,
        IReadOnlyList<AgentDefinition> agents, WorkerProfileCatalog profiles, ConductorAutonomyPolicy? policy,
        ConductorChildEnvironment? inherited = null, WorkerSandboxOptions? sandbox = null)
    {
        inherited ??= ConductorChildEnvironment.Capture();
        sandbox ??= WorkerSandboxOptions.FromEnvironment();
        var directory = workspace.ResolveExecutionDirectory(goal.Id);
        policy ??= ConductorAutonomyPolicy.LoadFromOrchestratorDirectory(new DirectoryInfo(workspace.OrchestratorDirectory));
        var providers = WorkerProviderCatalog.Default();
        var contexts = new List<EnvironmentProbeContext>();
        foreach (var task in goal.Tasks.Where(task => task.Status == WorkTaskStatus.Assigned ||
                     task.Status == WorkTaskStatus.Running && task.LastProcess is null))
        {
            var profile = task.Status == WorkTaskStatus.Running && task.LastDispatch is { } dispatch
                ? dispatch.WorkerName
                : WorkerProfileDispatcher.ResolveSubscriptionProfileName(
                    agents.Single(agent => agent.Id == task.AssignedAgentId), goal, task, profiles, sandbox,
                    cascadeTesterCheapFirst: policy.CascadeTesterCheapFirst,
                    cascadeCheapModelAlias: policy.CascadeCheapModelAlias,
                    cascadeMechanicalReworkCheap: policy.CascadeMechanicalReworkCheap);
            var resolvedProvider = task.Status == WorkTaskStatus.Running && task.LastDispatch is { WorkerProviderKind: not ProviderKind.Unknown } prepared
                ? providers.Resolve(prepared.WorkerProviderKind) : providers.ResolveProfile(profile);
            var provider = BackgroundDispatchRunner.IsGrokCliProfile(profile) ? WorkerSandboxProvider.Grok
                : BackgroundDispatchRunner.ResolveSandboxProvider(resolvedProvider);
            var local = profile.Equals("local", StringComparison.OrdinalIgnoreCase);
            var lowIntegrity = BackgroundDispatchRunner.ShouldUseOsSandbox(sandbox.Enabled, local, task.RequiredRole, provider);
            contexts.Add(Create(inherited, task.Status == WorkTaskStatus.Running ? task.LastDispatch!.WorkingDirectory : directory,
                lowIntegrity, !local));
        }
        // Startup can precede task assignment; admission repeats against the selected worker context.
        return contexts.Count == 0 ? [new(directory, inherited)] : contexts;
    }

    internal static EnvironmentProbeContext Create(ConductorChildEnvironment inherited, string directory,
        bool lowIntegrity, bool disableSharedCompilation) => new(directory,
            WorkerToolEnvironment.Prepare(inherited, Path.Combine(directory, ".mcg-sandbox"), lowIntegrity, disableSharedCompilation));
}
