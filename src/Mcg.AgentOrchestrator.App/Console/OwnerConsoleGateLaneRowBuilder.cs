using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.Infrastructure;
using static Mcg.AgentOrchestrator.App.OwnerConsole.OwnerConsoleGateProgress;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal static class OwnerConsoleGateLaneRowBuilder
{
    internal static OwnerConsoleGateLane Row(string name, IReadOnlyList<OwnerConsoleGateManifestLane> manifest,
        Dictionary<string, double[]> samplesByLane, Dictionary<string, string>? skipped,
        ILookup<string, Observation> observationsByLane, ILookup<string, Observation> remoteByLane,
        AcceptanceGateLanePlan? plan, DateTimeOffset now, IReadOnlyDictionary<string, double>? quietSeconds)
    {
        var lane = manifest.FirstOrDefault(lane => lane.Name == name);
        var samples = samplesByLane.GetValueOrDefault(name) ?? [];
        double? median = samples.Length == 0 ? lane?.EstimatedSerialSeconds :
            (samples[(samples.Length - 1) / 2] + samples[samples.Length / 2]) / 2;
        if (skipped?.TryGetValue(name, out var reason) == true)
            return new OwnerConsoleGateLane(name, OwnerConsoleGateLaneState.Skipped, "skipped", "",
                TimeSpan.Zero, median, samples.Length == 0, IsManifestLane: lane is not null, SkipReason: reason, IsScheduled: false);
        var observations = observationsByLane[name].ToArray();
        var remoteLane = remoteByLane[name].ToArray();
        var offer = remoteLane.LastOrDefault(item => item.Get("decision") == "offer");
        var latest = observations.Concat(remoteLane.Where(item =>
                item.Get("decision") == "offer" || item.Get("outcome") is "accepted" or "completed"))
            .OrderBy(item => item.Order).LastOrDefault();
        var noSlot = remoteLane.LastOrDefault();
        var waiting = noSlot?.Event.Detail.StartsWith("REMOTE_LANE ", StringComparison.Ordinal) == true &&
            noSlot.Get("outcome") == "offer-skipped-no-executor" &&
            (latest is null || noSlot.Order > latest.Order);
        var state = latest?.Get("phase") switch
        {
            "verification-check" or "remote-shard-started" => OwnerConsoleGateLaneState.Running,
            "shard-complete" or "remote-shard-complete" or "check-complete" => OwnerConsoleGateLaneState.Done,
            "cache-hit" or "reuse" => OwnerConsoleGateLaneState.Cached,
            _ => offer is null ? latest is null ? OwnerConsoleGateLaneState.Planned : OwnerConsoleGateLaneState.Queued : OwnerConsoleGateLaneState.Running
        };
        if (latest?.Event.EventKind == "remote-lane" && latest.Get("outcome") is "accepted" or "completed") state = OwnerConsoleGateLaneState.Done;
        if (waiting) state = OwnerConsoleGateLaneState.Queued;
        var elapsed = TimeSpan.FromMilliseconds(latest?.Number("elapsed_ms") ?? 0);
        if (state == OwnerConsoleGateLaneState.Running)
            elapsed += Nonnegative(now - (latest?.Event.Timestamp ?? offer!.Event.Timestamp));
        var executor = latest?.Get("phase") is "remote-shard-started" or "remote-shard-complete"
            ? remoteLane.LastOrDefault(item => item.Get("executor").Length > 0 && item.Get("outcome") != "offer-skipped-no-executor")?.Get("executor") ??
                observations.LastOrDefault(item => item.Get("executor").Length > 0)?.Get("executor") : null;
        if (latest?.Event.EventKind == "remote-lane" && latest.Get("executor").Length > 0) executor = latest.Get("executor");
        var slot = Regex.Match(latest?.Get("heartbeat") ?? "", @"activity-(\d+)");
        var location = waiting ? "no executor slot" : latest is null ? "" : executor ?? (slot.Success ? "local slot " + slot.Groups[1].Value :
            latest?.Get("decision") == "offer" || latest?.Get("phase") is "remote-shard-started" or "remote-shard-complete" ? "remote" : "local");
        double? quiet = null;
        if (state == OwnerConsoleGateLaneState.Running && latest?.Get("phase") == "verification-check")
        {
            if (quietSeconds?.TryGetValue(name, out var supplied) == true ||
                quietSeconds?.TryGetValue(latest.Get("target"), out supplied) == true) quiet = supplied;
            else quiet = SecondsSinceOutput(now, observations);
        }
        return new OwnerConsoleGateLane(name, state, latest?.Event.EventKind == "remote-lane" && latest.Get("outcome") == "accepted" ? "passed" : latest?.Get("outcome") is "passed" or "failed" ?
            latest.Get("outcome") : "unknown", location, elapsed, median, samples.Length == 0, quiet, lane is not null,
            latest?.Get("target"), observations.LastOrDefault(item => AttemptFromHeartbeat(item.Get("heartbeat")).Length > 0)?.Get("heartbeat"),
            IsAttributionTarget(latest?.Get("target") ?? name),
            TreeRevision: latest?.Get("tree"), EvidenceKind: latest?.Get("evidence_kind"),
            ReproductionMemberId: latest?.Get("reproduction_member"),
            IsScheduled: plan?.Scheduled.Contains(name, StringComparer.Ordinal) == true);
    }

    private static double? SecondsSinceOutput(DateTimeOffset now, Observation[] observations)
    {
        var samples = observations.Where(item => item.Get("phase") == "verification-check" &&
            item.Get("output_bytes").Length > 0 && item.Get("child_pid") is not ("" or "unknown")).ToArray();
        if (samples.Length == 0) return null;
        var newest = samples[^1];
        var start = newest.Event.Timestamp;
        foreach (var item in samples.Reverse())
        {
            if (item.Get("child_pid") != newest.Get("child_pid") || item.Get("output_bytes") != newest.Get("output_bytes")) break;
            start = item.Event.Timestamp;
        }
        return Math.Max(0, (now - start).TotalSeconds);
    }

    private static bool IsAttributionTarget(string target) =>
        target.StartsWith("reviewer focused evidence: ", StringComparison.Ordinal) ||
        target.StartsWith("reviewer mapped project evidence: ", StringComparison.Ordinal);
}
