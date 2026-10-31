using System.Globalization;
using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Supplemental liveness belongs to the activation census, not acceptance capacity accounting.
internal static class ActivationAdmissionPreparingAttemptCensus
{
    internal static ActivationAdmissionLiveGateTally Evaluate(
        IReadOnlyList<ConductorParallelAcceptanceAttempt> activeAttempts,
        IReadOnlyList<GateLoadContextProbe.LiveGateOccupant> liveGates,
        DateTimeOffset now) => Evaluate(activeAttempts, liveGates, now,
            attempt => AcceptanceHolderIdentity.Classify(attempt.OwnerProcessId,
                attempt.OwnerProcessStartedAt, attempt.OwnerExecutablePath));

    internal static ActivationAdmissionLiveGateTally Evaluate(
        IReadOnlyList<ConductorParallelAcceptanceAttempt> activeAttempts,
        IReadOnlyList<GateLoadContextProbe.LiveGateOccupant> liveGates,
        DateTimeOffset now,
        Func<ConductorParallelAcceptanceAttempt, SpawnTrackedProcessStatus> classifyOwner)
    {
        var attempts = activeAttempts.DistinctBy(attempt => attempt.AttemptId, StringComparer.Ordinal).ToArray();
        var live = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var gate in liveGates.Where(gate => gate.CountsAsAcceptanceOccupant))
        {
            // Multiple checks and the attempt heartbeat represent one acceptance attempt.
            var attempt = attempts.FirstOrDefault(candidate => Represents(gate, candidate));
            live.Add(attempt is not null ? $"attempt:{attempt.AttemptId}" :
                !string.IsNullOrWhiteSpace(gate.RunId) ? $"attempt:{gate.RunId.Trim()}" : gate.Identity);
        }

        var preparing = 0;
        foreach (var attempt in attempts)
        {
            var key = $"attempt:{attempt.AttemptId}";
            if (live.Contains(key) || attempt.Outcome != ConductorParallelAcceptanceAttemptOutcome.Running ||
                !HasFreshRunningHeartbeat(attempt.HeartbeatPath, now) ||
                classifyOwner(attempt) != SpawnTrackedProcessStatus.LiveMatch) continue;
            if (live.Add(key)) preparing++;
        }
        return new(live.Count, preparing);
    }

    private static bool Represents(GateLoadContextProbe.LiveGateOccupant gate,
        ConductorParallelAcceptanceAttempt attempt) =>
        (!string.IsNullOrWhiteSpace(gate.RunId) &&
            string.Equals(gate.RunId.Trim(), attempt.AttemptId, StringComparison.OrdinalIgnoreCase)) ||
        (gate.ProcessId is > 0 && gate.ProcessId.Value == attempt.OwnerProcessId) ||
        (!string.IsNullOrWhiteSpace(gate.GoalId) &&
            string.Equals(gate.GoalId, attempt.GoalId, StringComparison.OrdinalIgnoreCase));

    private static bool HasFreshRunningHeartbeat(string path, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var document = JsonDocument.Parse(stream);
            var root = document.RootElement;
            return root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("kind", out var kind) && kind.ValueKind == JsonValueKind.String &&
                string.Equals(kind.GetString(), ConductorParallelAcceptanceAttemptCoordinator.GateDispatchKind,
                    StringComparison.Ordinal) &&
                root.TryGetProperty("state", out var state) && state.ValueKind == JsonValueKind.String &&
                string.Equals(state.GetString(), "running", StringComparison.OrdinalIgnoreCase) &&
                root.TryGetProperty("lastProgressAt", out var progress) && progress.ValueKind == JsonValueKind.String &&
                DateTimeOffset.TryParse(progress.GetString(), CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var observed) &&
                observed <= now && now - observed <= GateLoadContextProbe.LiveGateHeartbeatFreshness;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or
            ArgumentException or NotSupportedException)
        {
            return false;
        }
    }
}
