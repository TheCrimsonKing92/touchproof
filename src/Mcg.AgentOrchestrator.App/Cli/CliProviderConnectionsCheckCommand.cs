using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class CliProviderConnectionsCheckCommand
{
    internal static bool IsCommand(IReadOnlyList<string> args) =>
        CliProviderConnectionsCommand.IsCommand(args) && !CliProviderConnectionsCommand.IsListing(args);

    internal static async Task<int> ExecuteWorkspaceAsync(IReadOnlyList<string> args, OrchestratorWorkspace workspace,
        Action prepareCheck)
    {
        try
        {
            return await ExecuteAsync(args, WorkerProfileStore.Load(workspace.WorkerProfilePath), () =>
            {
                prepareCheck();
                return new SqliteOrchestratorStateRepository(workspace.SqliteStatePath);
            }, new ProviderConnectionStatusAdapters(workspace.RootDirectory), Console.Out, Console.Error);
        }
        catch (Exception)
        {
            await Console.Error.WriteLineAsync("Provider connection configuration could not be read.");
            return 1;
        }
    }

    internal static async Task<int> ExecuteAsync(IReadOnlyList<string> args, WorkerProfileCatalog catalog,
        Func<IProviderConnectionEvidenceStore> openStore, ProviderConnectionStatusAdapters adapters,
        TextWriter output, TextWriter error, TimeProvider? time = null, Func<ClaudeCliAuthState>? claudeProbe = null,
        CancellationToken cancellationToken = default)
    {
        if (!CliProviderConnectionsCommand.TryParse(args, out _, out var profile, out var json) || profile is null)
        {
            await error.WriteLineAsync(CliProviderConnectionsCommand.Usage);
            return 1;
        }

        // Validate before opening a writer, initializing schema or executing a probe.
        var normalized = LunaLaneNames.NormalizeProfileName(profile);
        var matches = catalog.Profiles.Count(p => string.Equals(LunaLaneNames.NormalizeProfileName(p.Name),
            normalized, StringComparison.OrdinalIgnoreCase));
        if (matches != 1)
        {
            await error.WriteLineAsync("Select one configured, unambiguous profile. " + CliProviderConnectionsCommand.Usage);
            return 1;
        }

        return await CliProviderConnectionsCommand.ExecuteReportAsync(() =>
        {
            var store = openStore();
            return new ProviderConnectionReadinessService(catalog, store, adapters, time, claudeProbe)
                .CheckAsync(profile, store, cancellationToken);
        }, json, output, error, time, cancellationToken);
    }
}
