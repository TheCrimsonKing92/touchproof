using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed class AdoptedBuildPushTrigger(
    Func<ConductorAutonomyPolicy> readPolicy, Action<string, Action<string>> push,
    Action<string> append, Action<string> attention, Action<string>? publishRepositories = null,
    Action? sweepOutcomes = null)
{
    internal AdoptedBuildPushTrigger(
        Func<ConductorAutonomyPolicy> readPolicy, Action<string> push,
        Action<string> append, Action<string> attention, Action<string>? publishRepositories = null,
        Action? sweepOutcomes = null)
        : this(readPolicy, (commit, _) => push(commit), append, attention, publishRepositories, sweepOutcomes) { }

    internal void OnFinalized(string commit, string pending, string failed, string stopped = "")
    {
        Publish(commit, pending, failed, stopped);
        try { sweepOutcomes?.Invoke(); }
        catch (Exception ex)
        {
            AdoptedBuildPublicationReporting.Try(
                () => append($"COST_GOAL_OUTCOME_SWEEP_FAILED source=adoption commit={commit} reason={ex.GetType().Name}"),
                "cost-outcome-adoption-sweep");
        }
    }

    private void Publish(string commit, string pending, string failed, string stopped)
    {
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
        if (failed.Length != 0) { Hold("fleet-adoption-failed"); return; }
        try
        {
            push(commit, line => append(line.StartsWith("ADOPTED_BUILD_PUSHED ", StringComparison.Ordinal)
                ? line + $" stopped={stopped} pending={pending}" : line));
        }
        catch (Exception)
        {
            Hold("service-failed");
        }
        try { publishRepositories?.Invoke(commit); }
        catch (OperationCanceledException) { throw; }
        catch (Exception)
        {
            AdoptedBuildPublicationReporting.Try(
                () => append($"ADOPTED_BUILD_PUSH_HELD commit={commit} reason=repository-publication-failed"),
                "repository-trigger-held-event");
            AdoptedBuildPublicationReporting.Try(
                () => attention($"Non-default repository publication failed. commit={commit} reason=repository-publication-failed. " +
                    "Next: inspect the landing records and manually publish the non-default repositories from their checkouts."),
                "repository-trigger-held-attention");
        }
    }

    internal static OrchestratorWorkspace ResolveHomeWorkspace(OrchestratorProjectRegistry registry)
    {
        var root = OrchestratorHome.ResolveForProcess().SourceRootDirectory
            ?? throw new InvalidOperationException("Adopted build publication requires a home source root.");
        var project = registry.ListProjects().SingleOrDefault(project => string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(project.RootDirectory)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)), StringComparison.OrdinalIgnoreCase));
        return project?.ResolveWorkspace() ?? OrchestratorWorkspace.ForDirectory(
            root, root, OrchestratorWorkspace.DefaultTenantName, registry);
    }

    internal static AdoptedBuildPushTrigger CreateDefault(Action<string> append, Action? sweepOutcomes = null)
    {
        var registry = OrchestratorProjectRegistry.CreateDefault();
        var workspace = ResolveHomeWorkspace(registry);
        var root = workspace.ExecutionDirectory;
        var pushGate = new object();
        var landings = new MultiRepositoryLandingRecordStore(workspace.OrchestratorDirectory);
        var publisher = new MultiRepositoryAdoptedBuildPublisher(new(workspace, registry),
            Path.Combine(root, AdoptedBuildLeakScanner.GenericPatternPath),
            AdoptedBuildLeakScanner.OwnerPatternPath(OrchestratorDataRoot.Resolve().RootDirectory), append,
            body => CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory)
                .RaiseAsync(Mcg.AgentOrchestrator.Core.CollaborationItemType.Decision, null,
                    "Adopted build push held", body, "adopted-build-push-held:" + body.Split('\n')[0]).GetAwaiter().GetResult());
        return new(() => ConductorAutonomyPolicy.LoadFromOrchestratorDirectory(new DirectoryInfo(workspace.OrchestratorDirectory)),
            (commit, sink) =>
            {
                lock (pushGate)
                    AdoptedBuildPushService.CreateDefault(root, sink).Push(commit, branch: workspace.IntegrationBranch);
            }, append,
            body => Mcg.AgentOrchestrator.Infrastructure.CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory)
                .RaiseAsync(Mcg.AgentOrchestrator.Core.CollaborationItemType.Decision, null,
                    "Adopted build push held", body, "adopted-build-push-policy").GetAwaiter().GetResult(),
            commit => publisher.Publish(landings.FindByLandedHomeCommit(commit)), sweepOutcomes);
    }
}
