using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class GateStatusTextView
{
    public static void Print(IReadOnlyList<GateHeartbeatStatus> statuses, DateTimeOffset? observedAt = null,
        int remoteWaits = 0, IReadOnlyList<GateStatusRemoteWaitDetail.Row>? remoteWaitRows = null)
    {
        Console.WriteLine("Gate status:");
        var now = observedAt ?? DateTimeOffset.UtcNow;
        var running = 0;
        var stalled = 0;
        foreach (var status in statuses)
        {
            var verdict = GateLivenessVerdict.Classify(status, now);
            if (verdict.Liveness == GateLiveness.Running)
                running++;
            else if (verdict.Liveness == GateLiveness.Stalled)
                stalled++;

            if (!status.IsAvailable || status.Snapshot is null)
            {
                Console.WriteLine($"slot-{status.SlotIndex}: unavailable reason={status.UnavailableReason ?? "unknown"} heartbeat={status.Path} verdict={verdict.Label}");
                continue;
            }

            var snapshot = status.Snapshot;
            Console.WriteLine(
                $"slot-{status.SlotIndex}: goal={ShortGateGoal(snapshot.GoalId)} phase={snapshot.Phase} state={snapshot.State} " +
                $"elapsed={FormatGateDuration(status.HeartbeatAge is null ? TimeSpan.Zero : snapshot.LastObservedAt - snapshot.StartedAt)} " +
                $"target={Quote(snapshot.CurrentTarget)} child_pid={snapshot.ChildPid?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown"} " +
                $"heartbeat_age={FormatGateDuration(status.HeartbeatAge)} idle_for={FormatGateDuration(status.IdleDuration)} output_bytes={snapshot.OutputBytes} " +
                $"heartbeat={status.Path} verdict={verdict.Label}");
        }

        var hostVerdict = GateHostVerdict.Label(running, stalled, remoteWaits);
        Console.WriteLine(
            $"host: verdict={hostVerdict}" + (remoteWaits > 0 ? $" remote_waits={remoteWaits}" : "") +
            $" running={running} stalled={stalled} slots={statuses.Count}; covers per-goal and cohort gates alike" +
            (hostVerdict == "NONE" ? "; NONE covers build-slot lease occupancy only; a live acceptance coordinator or remote receipt finalization may still be running; verify the exact attempt and process identity before concluding a terminal state; do not restart acceptance solely because observation is absent" :
                hostVerdict == "RUNNING (remote)" ? "; gate waiting on remote lane; no build-slot process to check" : ""));

        if (remoteWaitRows is not null)
            foreach (var row in remoteWaitRows)
                Console.WriteLine(FormatRemoteWait(row));
    }

    private static string FormatRemoteWait(GateStatusRemoteWaitDetail.Row row) =>
        $"remote-wait: goal={ShortGateGoal(row.GoalId)} attempt={row.AttemptId} " +
        $"owner_pid={row.OwnerProcessId?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown"} " +
        $"candidate={row.CandidateSha ?? "unknown"} " +
        $"last_progress={row.LastProgressAt?.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture) ?? "unknown"} " +
        $"age={FormatGateDuration(row.Age)} outcome={row.Outcome?.ToString() ?? "unknown"} " +
        $"owner={row.Owner.ToString().ToLowerInvariant()} state={row.State}" +
        (row.IdentityVerified ? "" : " identity=unverified");

    private static string ShortGateGoal(string? goalId) =>
        string.IsNullOrWhiteSpace(goalId) ? "unknown" : goalId.Length <= 8 ? goalId : goalId[..8];

    private static string FormatGateDuration(TimeSpan? duration)
    {
        if (duration is null)
            return "unknown";

        var value = duration.Value < TimeSpan.Zero ? TimeSpan.Zero : duration.Value;
        return value.TotalMinutes >= 1
            ? $"{value.TotalMinutes:0.#}m"
            : $"{value.TotalSeconds:0.#}s";
    }

    private static string Quote(string value) =>
        value.IndexOfAny([' ', '\t', '\r', '\n', '"']) < 0
            ? value
            : $"\"{value.Replace("\"", "\\\"", StringComparison.Ordinal)}\"";
}
