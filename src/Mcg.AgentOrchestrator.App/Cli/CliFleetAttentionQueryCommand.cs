using System.Globalization;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.App.OwnerConsole;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class CliFleetAttentionQueryCommand
{
    internal const string Usage = "Usage: attention fleet [--json] [--limit N]";

    // Recognize intent, including malformed flags, before writable startup.
    internal static bool IsCommand(IReadOnlyList<string> args) => args.Count >= 2 &&
        args[0].Equals("attention", StringComparison.OrdinalIgnoreCase) &&
        args[1].Equals("fleet", StringComparison.OrdinalIgnoreCase);

    internal static (bool Json, int Limit) Parse(IReadOnlyList<string> args)
    {
        if (!IsCommand(args)) throw new ArgumentException(Usage);
        var json = false;
        var limit = 50;
        var hasLimit = false;
        for (var index = 2; index < args.Count; index++)
        {
            if (args[index] == "--json" && !json) json = true;
            else if (args[index] == "--limit" && !hasLimit && ++index < args.Count &&
                int.TryParse(args[index], NumberStyles.None, CultureInfo.InvariantCulture, out limit)) hasLimit = true;
            else throw new ArgumentException(Usage);
        }
        return (json, limit);
    }

    internal static async Task<int> ExecuteAsync(IReadOnlyList<string> args, string repositoryRoot,
        string? tenant, OrchestratorProjectRegistry registry, TextWriter output,
        OrchestratorWorkspace? launchWorkspace = null, CancellationToken cancellationToken = default,
        TimeProvider? time = null)
    {
        var options = Parse(args);
        cancellationToken.ThrowIfCancellationRequested();
        var catalog = OwnerConsoleWorkspaceCatalog.ReadWorkspaceCatalog(repositoryRoot, tenant, registry,
            launchWorkspace, cancellationToken);
        var result = await FleetAttentionReader.ReadAsync(catalog, options.Limit,
            (time ?? TimeProvider.System).GetUtcNow(), cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        await output.WriteLineAsync(options.Json
            ? AgentReadEnvelope.Serialize("mcg.fleet-attention", 1, result,
                new JsonSerializerOptions(JsonSerializerDefaults.Web), time)
            : FleetAttentionTextView.Render(result)).ConfigureAwait(false);
        return result.Complete ? 0 : 1;
    }
}
