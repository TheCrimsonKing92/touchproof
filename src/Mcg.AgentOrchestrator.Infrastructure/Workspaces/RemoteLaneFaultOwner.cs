using System.Text.Json;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class RemoteLaneFaultOwner
{
    internal static string? Resolve(string? owner, JsonElement? lastStatus, string attemptId)
    {
        // A result folder may still contain a status from an earlier attempt at the same lane.
        if (owner is null || lastStatus is not { ValueKind: JsonValueKind.Object } status ||
            !status.TryGetProperty("attemptId", out var attempt) || attempt.ValueKind != JsonValueKind.String ||
            !attempt.ValueEquals(attemptId) ||
            !status.TryGetProperty("runnerDiagnosis", out var diagnosis) || diagnosis.ValueKind != JsonValueKind.Array)
            return owner;

        foreach (var line in diagnosis.EnumerateArray())
            if (line.ValueKind == JsonValueKind.String &&
                line.GetString()!.Contains("FILTER FAILURE", StringComparison.Ordinal))
                return "runner";

        return owner;
    }
}
