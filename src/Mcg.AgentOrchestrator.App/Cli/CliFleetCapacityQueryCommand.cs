using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.App.OwnerConsole;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class CliFleetCapacityQueryCommand
{
    internal const string Usage = "Usage: fleet-capacity [--json]";

    internal static bool IsCommand(IReadOnlyList<string> args) => args.Count > 0 &&
        args[0].Equals("fleet-capacity", StringComparison.OrdinalIgnoreCase);

    internal static bool Parse(IReadOnlyList<string> args)
    {
        if (!IsCommand(args)) throw new ArgumentException(Usage);
        foreach (var arg in args.Skip(1))
        {
            if (arg.StartsWith('-') && arg != "--json")
                throw new ArgumentException($"Unknown option '{arg}'.{Environment.NewLine}{Usage}");
        }
        if (args.Count > 2 || args.Count == 2 && args[1] != "--json")
            throw new ArgumentException(Usage);
        return args.Count == 2;
    }

    internal static async Task<int> ExecuteAsync(IReadOnlyList<string> args, string repositoryRoot,
        string? tenant, OrchestratorProjectRegistry registry, TextWriter output,
        OrchestratorWorkspace? launchWorkspace = null, CancellationToken cancellationToken = default,
        TimeProvider? time = null)
    {
        var json = Parse(args);
        cancellationToken.ThrowIfCancellationRequested();
        var observedAt = (time ?? TimeProvider.System).GetUtcNow();
        var catalog = OwnerConsoleWorkspaceCatalog.ReadWorkspaceCatalog(repositoryRoot, tenant, registry,
            launchWorkspace, cancellationToken);
        var result = await FleetWorkerCapacityReader.ReadAsync(catalog, observedAt, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        await output.WriteLineAsync(json
            ? AgentReadEnvelope.Serialize("mcg.fleet-capacity", 1, result,
                new JsonSerializerOptions(JsonSerializerDefaults.Web), time)
            : FleetCapacityTextView.Render(result)).ConfigureAwait(false);
        return result.Complete ? 0 : 1;
    }
}
