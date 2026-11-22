using System.Text.Json;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class WorkerToolsTelemetry
{
    internal const string EnvironmentVariable = "WORKER_TOOLS_TELEMETRY";

    internal static string PathFor(string stdoutPath) =>
        Path.GetFullPath(Path.Combine(stdoutPath + ".wt", "telemetry.jsonl"));

    internal static IReadOnlyDictionary<string, long>? Read(AgentRole role, TaskDispatchRecord? dispatch,
        string stdoutPath, out string? diagnostic)
    {
        diagnostic = null;
        if (!WorkerToolsPromptSection.AppliesTo(role) || dispatch is null)
            return null;

        var counts = new SortedDictionary<string, long>(StringComparer.Ordinal);
        var errors = new List<string>();
        var outputs = role == AgentRole.Planner
            ? PlannerSampleDispatcher.CreateArtifacts(stdoutPath, dispatch.PlannerSampleCount)
                .Select(artifact => artifact.StandardOutputPath).Prepend(stdoutPath)
            : [stdoutPath];
        foreach (var output in outputs)
        {
            var path = PathFor(output);
            var invalid = 0;
            try
            {
                foreach (var line in File.ReadLines(path))
                {
                    if (ReadSubcommand(line) is { } command)
                        counts[command] = counts.GetValueOrDefault(command) + 1;
                    else
                        invalid++;
                }
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                errors.Add($"'{path}': {ex.Message}");
            }
            if (invalid > 0)
                errors.Add($"'{path}': skipped {invalid} malformed telemetry records");
        }

        if (errors.Count > 0)
            diagnostic = $"WORKER_TOOLS_TELEMETRY_READ_FAILED {string.Join("; ", errors)}";
        return counts.Count == 0 ? null : counts;
    }

    private static string? ReadSubcommand(string line)
    {
        try
        {
            using var json = JsonDocument.Parse(line);
            return json.RootElement.ValueKind == JsonValueKind.Object &&
                json.RootElement.TryGetProperty("subcommand", out var value) &&
                value.ValueKind == JsonValueKind.String && value.GetString() is { } command &&
                !string.IsNullOrWhiteSpace(command) ? command : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
