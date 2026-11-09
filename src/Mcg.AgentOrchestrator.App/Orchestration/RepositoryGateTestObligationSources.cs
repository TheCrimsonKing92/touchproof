using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class RepositoryGateTestObligationSources
{
    internal static (IReadOnlyList<GateTestObligationEvaluator.Source> Sources, string? PendingDetail) Read(
        Goal goal, string key, string orchestratorDirectory, Func<string?>? currentTuple)
    {
        var pending = $"repository {key}: no gate result for the current candidate tuple";
        if (goal.Repositories is null || !goal.Repositories.Keys.Contains(key, StringComparer.Ordinal))
            return ([], pending);

        string? canonical;
        try { canonical = currentTuple?.Invoke(); }
        catch (Exception) { return ([], pending); }
        if (!RepositoryCandidateTuple.TryParse(canonical, out var tuple)) return ([], pending);

        try
        {
            var store = new MultiRepositoryAcceptanceReceiptStore(orchestratorDirectory);
            var receipt = store.TryRead(goal.Id);
            if (receipt is null || !receipt.IsCurrentFor(tuple!)) return ([], pending);
            var outcome = receipt.Outcomes.SingleOrDefault(item => string.Equals(item.Key, key, StringComparison.Ordinal));
            if (outcome is null) return ([], pending);

            // Bind the reference to this goal, attempt and repository, not merely to a nearby JSON file.
            var path = Path.GetFullPath(outcome.EvidenceReference, orchestratorDirectory);
            var attempt = Path.GetFileName(Path.GetDirectoryName(path));
            if (!Guid.TryParseExact(attempt, "N", out _)) return ([], pending);
            var expected = Path.GetFullPath(Path.Combine(store.DirectoryPath, goal.Id.Value, attempt, key + ".json"));
            if (!string.Equals(path, expected, OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) return ([], pending);

            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("TestResultPaths", out var pathsProperty) ||
                pathsProperty.ValueKind != JsonValueKind.Array || pathsProperty.GetArrayLength() == 0)
                return ([], pending);
            var paths = new List<string>();
            foreach (var item in pathsProperty.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString()))
                    return ([], pending);
                var resultPath = Path.GetFullPath(item.GetString()!, orchestratorDirectory);
                if (!File.Exists(resultPath)) return ([], pending);
                paths.Add(resultPath);
            }
            return ([new($"repository:{key}:{attempt}", receipt.RecordedAt, paths, goal.AuthoritativeBrief.Version)], null);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or IOException or UnauthorizedAccessException)
        {
            return ([], pending);
        }
    }
}
