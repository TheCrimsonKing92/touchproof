using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class InstalledManifestProbeReport
{
    internal static IReadOnlyList<string> Lines(string learnedManifestJson, string projectRoot,
        IEnvironmentProbeRunner runner, CancellationToken cancellationToken = default)
    {
        EnvironmentContract contract;
        try
        {
            contract = EnvironmentContract.FromManifest(learnedManifestJson);
        }
        catch (JsonException)
        {
            return [];
        }

        var environment = ConductorChildEnvironment.Capture();
        var lines = new List<string>();
        foreach (var tool in contract.Tools)
        {
            string outcome;
            try
            {
                var result = runner.RunAsync(tool, environment, projectRoot, cancellationToken)
                    .GetAwaiter().GetResult();
                if (result.Passed) continue;
                outcome = result.Outcome;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                outcome = "error";
            }
            lines.Add($"ProbeUnresolved {OneLine(tool.Name)} probe={OneLine(tool.ProbeCommand)} outcome={OneLine(outcome)}");
        }
        return lines;
    }

    private static string OneLine(string value) => value.Replace("\r", "\\r").Replace("\n", "\\n");
}
