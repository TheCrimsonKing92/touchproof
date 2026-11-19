using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal sealed record OwnerConsoleProviderConnection(ProviderConnectionProfileReport Report, ProviderSignInTarget? SignIn);

internal interface IProviderConnectionsConsoleGateway
{
    Task<IReadOnlyList<OwnerConsoleProviderConnection>> ListAsync(CancellationToken token);
    Task<OwnerConsoleProviderConnection> CheckAsync(string profileId, CancellationToken token);
    ProviderSignInTarget? GetSignInTarget(string profileId);
}

internal sealed class ProviderConnectionsConsoleGateway(
    Func<WorkerProfileCatalog> loadProfiles,
    IProviderConnectionEvidenceQueries queries,
    Func<IProviderConnectionEvidenceStore> openStore,
    ProviderConnectionStatusAdapters adapters,
    TimeProvider clock) : IProviderConnectionsConsoleGateway
{
    internal static ProviderConnectionsConsoleGateway ForWorkspace(OrchestratorWorkspace workspace,
        IProviderConnectionEvidenceQueries queries, TimeProvider clock) => new(
        () => WorkerProfileStore.Load(workspace.WorkerProfilePath), queries,
        () =>
        {
            WorkspaceStoreSetup.EnsureState(workspace);
            return new SqliteOrchestratorStateRepository(workspace.SqliteStatePath);
        }, new ProviderConnectionStatusAdapters(workspace.RootDirectory, time: clock), clock);

    public async Task<IReadOnlyList<OwnerConsoleProviderConnection>> ListAsync(CancellationToken token)
    {
        var catalog = loadProfiles();
        var report = await new ProviderConnectionReadinessService(catalog, queries, adapters, clock).ListAsync(token);
        return report.Profiles.Select(profile => new OwnerConsoleProviderConnection(
            profile, ProviderSignInTarget.ForProfile(catalog.GetRequired(profile.ProfileId)))).ToArray();
    }

    public async Task<OwnerConsoleProviderConnection> CheckAsync(string profileId, CancellationToken token)
    {
        var catalog = loadProfiles();
        var readiness = new ProviderConnectionReadinessService(catalog, queries, adapters, clock);
        var profile = readiness.Select(profileId);
        token.ThrowIfCancellationRequested();
        var store = openStore();
        var report = await new ProviderConnectionReadinessService(catalog, store, adapters, clock)
            .CheckAsync(profileId, store, token);
        return new(report.Profiles.Single(), ProviderSignInTarget.ForProfile(profile));
    }

    public ProviderSignInTarget? GetSignInTarget(string profileId)
    {
        var readiness = new ProviderConnectionReadinessService(loadProfiles(), queries, adapters, clock);
        return ProviderSignInTarget.ForProfile(readiness.Select(profileId));
    }
}
