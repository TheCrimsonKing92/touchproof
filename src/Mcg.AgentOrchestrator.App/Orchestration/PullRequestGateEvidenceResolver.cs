using System.Text.Json;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class PullRequestGateEvidenceResolver
{
    public static (string AttemptId, IReadOnlyList<string> CheckNames, string RedactionSource) Resolve(
        OrchestratorWorkspace workspace, Goal goal, string candidate)
    {
        var matches = new List<(string Id, DateTimeOffset Started, string Source)>();
        foreach (var evidence in GoalTerminalReconciliationEvidenceResolver.ResolveForGoal(
                     Path.Combine(workspace.OrchestratorDirectory, "acceptance-gate-attempts"), goal.Id))
        {
            if (evidence.State is not (GoalTerminalReconciliationEvidenceState.Present or
                    GoalTerminalReconciliationEvidenceState.MissingByInProcessProtocol) ||
                evidence.AcceptancePassed != true || !string.Equals(evidence.TypedKind, "accepted", StringComparison.OrdinalIgnoreCase)) continue;
            using var metadata = JsonDocument.Parse(File.ReadAllText(evidence.AttemptMetadataPath));
            var root = metadata.RootElement;
            if (Text(root, "goalId") != goal.Id.Value ||
                !string.Equals(Text(root, "branchHeadSha"), candidate, StringComparison.OrdinalIgnoreCase) ||
                Text(root, "kind") != ConductorParallelAcceptanceAttemptCoordinator.GateDispatchKind)
                continue;
            var focused = Read(root, "focusedEvidenceRequest");
            if (focused.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null) &&
                (focused.ValueKind != JsonValueKind.String || !string.IsNullOrWhiteSpace(focused.GetString()))) continue;
            var rawResult = File.ReadAllText(evidence.TypedResultPath);
            var startedText = Read(root, "startedAt");
            var started = startedText.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(startedText.GetString(), out var parsed)
                ? parsed : DateTimeOffset.MinValue;
            matches.Add((evidence.AttemptId, started, rawResult));
        }
        var latest = matches.OrderByDescending(match => match.Started).ThenByDescending(match => match.Id, StringComparer.Ordinal).FirstOrDefault();
        if (latest.Id is null) throw new InvalidOperationException("No accepted solo gate attempt is bound to the candidate.");
        using var result = JsonDocument.Parse(latest.Source);
        var checks = Read(Read(result.RootElement, "acceptance"), "checkNames");
        if (checks.ValueKind != JsonValueKind.Array || checks.GetArrayLength() == 0)
            throw new InvalidOperationException("Accepted gate receipt has no check names.");
        var names = checks.EnumerateArray()
            .Select(check => check.ValueKind == JsonValueKind.String ? check.GetString() : null).ToArray();
        if (names.Any(string.IsNullOrWhiteSpace))
            throw new InvalidOperationException("Accepted gate receipt has an unnamed check.");
        return (latest.Id, names.Select(name => name!).ToArray(), latest.Source);
    }

    private static string? Text(JsonElement element, string name) =>
        Read(element, name) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;

    private static JsonElement Read(JsonElement element, string name)
    {
        if (element.ValueKind == JsonValueKind.Object)
            foreach (var property in element.EnumerateObject())
                if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return property.Value;
        return default;
    }
}
