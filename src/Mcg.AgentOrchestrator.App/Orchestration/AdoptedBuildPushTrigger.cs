using Mcg.AgentOrchestrator.Core.Conductor;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed class AdoptedBuildPushTrigger(
    Func<ConductorAutonomyPolicy> readPolicy, Action<string> push,
    Action<string> append, Action<string> attention)
{
    internal void OnFinalized(string commit, string pending, string failed)
    {
        if (pending.Length != 0 || failed.Length != 0) return;
        ConductorAutonomyPolicy policy;
        void Hold(string reason)
        {
            AdoptedBuildPublicationReporting.Try(
                () => append($"ADOPTED_BUILD_PUSH_HELD commit={commit} reason={reason}"), "trigger-held-event");
            AdoptedBuildPublicationReporting.Try(
                () => attention($"Adopted build publication held: {reason}. Next: push-adopted {commit}"), "trigger-held-attention");
        }
        try { policy = readPolicy(); }
        catch (Exception) { Hold("policy-unreadable"); return; }
        if (!policy.AdoptedBuildPushEnabled) return;
        try
        {
            push(commit);
        }
        catch (Exception)
        {
            Hold("service-failed");
        }
    }

    internal static AdoptedBuildPushTrigger CreateDefault(Action<string> append)
    {
        var root = OrchestratorHome.ResolveForProcess().SourceRootDirectory
            ?? throw new InvalidOperationException("Adopted build publication requires a home source root.");
        var registry = OrchestratorProjectRegistry.CreateDefault();
        var project = registry.ListProjects().SingleOrDefault(project => string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(project.RootDirectory)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)), StringComparison.OrdinalIgnoreCase));
        var workspace = project?.ResolveWorkspace() ?? OrchestratorWorkspace.ForDirectory(
            root, root, OrchestratorWorkspace.DefaultTenantName, registry);
        var service = AdoptedBuildPushService.CreateDefault(root, append);
        return new(() => ConductorAutonomyPolicy.LoadFromOrchestratorDirectory(new DirectoryInfo(workspace.OrchestratorDirectory)),
            commit => service.Push(commit, branch: workspace.IntegrationBranch), append,
            body => Mcg.AgentOrchestrator.Infrastructure.CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory)
                .RaiseAsync(Mcg.AgentOrchestrator.Core.CollaborationItemType.Decision, null,
                    "Adopted build push held", body, "adopted-build-push-policy").GetAwaiter().GetResult());
    }
}
