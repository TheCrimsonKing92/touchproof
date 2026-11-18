using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class CliProviderConnectionsCommand
{
    internal const string Usage = "Usage: provider-connections [--json] | provider-connections check --profile <profile-id> [--json]";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    internal static bool IsCommand(IReadOnlyList<string> args) => args.Count > 0 &&
        args[0].Equals("provider-connections", StringComparison.OrdinalIgnoreCase) && !CliCommandHelp.IsCommandSpecificHelp(args);

    internal static bool IsListing(IReadOnlyList<string> args) => IsCommand(args) &&
        !(args.Count > 1 && args[1].Equals("check", StringComparison.OrdinalIgnoreCase));

    internal static async Task<int> ExecuteWorkspaceAsync(IReadOnlyList<string> args, OrchestratorWorkspace workspace,
        Action? prepareCheck = null)
    {
        try
        {
            return await ExecuteAsync(args, WorkerProfileStore.Load(workspace.WorkerProfilePath), check =>
            {
                if (!check) return SqliteOrchestratorStateRepository.OpenReadOnly(workspace.SqliteStatePath);
                prepareCheck?.Invoke();
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
        Func<bool, IProviderConnectionEvidenceQueries> openStore, ProviderConnectionStatusAdapters adapters,
        TextWriter output, TextWriter error, TimeProvider? time = null, Func<ClaudeCliAuthState>? claudeProbe = null,
        CancellationToken cancellationToken = default)
    {
        if (!TryParse(args, out var check, out var profile, out var json))
        {
            await error.WriteLineAsync(Usage);
            return 1;
        }
        try
        {
            if (check)
            {
                // Validate before opening a writer, initializing schema or executing a probe.
                var matches = catalog.Profiles.Count(p => string.Equals(LunaLaneNames.NormalizeProfileName(p.Name),
                    LunaLaneNames.NormalizeProfileName(profile!), StringComparison.OrdinalIgnoreCase));
                if (matches != 1)
                {
                    await error.WriteLineAsync("Select one configured, unambiguous profile. " + Usage);
                    return 1;
                }
            }
            var store = openStore(check);
            var service = new ProviderConnectionReadinessService(catalog, store, adapters, time, claudeProbe);
            var report = check ? await service.CheckAsync(profile!, (IProviderConnectionEvidenceStore)store, cancellationToken) : await service.ListAsync(cancellationToken);
            await RenderAsync(report, json, output, time);
            return 0;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await error.WriteLineAsync("Provider connection report was cancelled.");
            return 1;
        }
        catch (Exception)
        {
            // Exception messages may contain provider output, paths or credential identities.
            await error.WriteLineAsync("Provider connection report could not be produced. Review local configuration and status tooling.");
            return 1;
        }
    }

    private static bool TryParse(IReadOnlyList<string> args, out bool check, out string? profile, out bool json)
    {
        check = args.Count > 1 && args[1].Equals("check", StringComparison.OrdinalIgnoreCase);
        profile = null;
        json = false;
        if (args.Count == 0 || !args[0].Equals("provider-connections", StringComparison.OrdinalIgnoreCase)) return false;
        for (var i = check ? 2 : 1; i < args.Count; i++)
        {
            if (args[i] == "--json" && !json) { json = true; continue; }
            if (args[i] == "--profile" && check && profile is null && i + 1 < args.Count &&
                !string.IsNullOrWhiteSpace(args[i + 1]) && !args[i + 1].StartsWith('-')) { profile = args[++i]; continue; }
            return false;
        }
        return !check || profile is not null;
    }

    internal static async Task RenderAsync(ProviderConnectionReport report, bool json, TextWriter output, TimeProvider? time = null)
    {
        if (json)
        {
            await output.WriteLineAsync(AgentReadEnvelope.Serialize(AgentReadEnvelope.ProviderConnectionsSchema,
                AgentReadEnvelope.ProviderConnectionsSchemaVersion, report, JsonOptions, time));
            return;
        }
        if (report.Profiles.Count == 0)
            await output.WriteLineAsync("No configured worker profiles. Review the effective worker profile catalog.");
        foreach (var profile in report.Profiles)
        {
            // JSON string quoting preserves canonical identity while escaping terminal controls.
            await output.WriteLineAsync($"{JsonSerializer.Serialize(profile.ProfileId)} provider={profile.Provider}");
            foreach (var observation in profile.Observations)
            {
                await output.WriteLineAsync(FormattableString.Invariant(
                    $"  context={observation.ExecutionContext} mode={observation.AuthenticationMode} source={observation.EvidenceSource} result={observation.Result} observedAt={observation.ObservedAt?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) ?? "unknown"} historical={observation.IsHistorical} ageSeconds={observation.AgeSeconds?.ToString("R", CultureInfo.InvariantCulture) ?? "unknown"}"));
                await output.WriteLineAsync($"    {observation.Reason} Next: {observation.NextAction}");
            }
        }
    }
}
