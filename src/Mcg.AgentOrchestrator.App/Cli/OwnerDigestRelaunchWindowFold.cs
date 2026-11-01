using System.Text.Json;

namespace Mcg.AgentOrchestrator.App.Cli;

internal sealed record RelaunchWindow(DateTimeOffset Scheduled, DateTimeOffset? Stopped,
    DateTimeOffset? FirstTick, DateTimeOffset? Adopted, string Status, IReadOnlyList<string> Issues,
    double? DrainMs, double? RestartMs, double? ProbationMs, double? TotalMs);

internal static class OwnerDigestRelaunchWindowFold
{
    // Process start timestamps and stop envelopes can differ slightly in capture precision.
    private static readonly TimeSpan ProcessStartTolerance = TimeSpan.FromSeconds(5);

    internal static IReadOnlyList<RelaunchWindow> Fold(IEnumerable<MeasurementLogRecord> orderedEvents)
    {
        var windows = new List<RelaunchWindow>();
        DateTimeOffset? scheduled = null, stopped = null, firstTick = null;

        void Close(DateTimeOffset? adopted, string? reason = null)
        {
            if (scheduled is null) return;
            var issues = new List<string>();
            if (stopped is null) issues.Add("missing stop");
            if (firstTick is null) issues.Add("missing first tick");
            if (adopted is null) issues.Add("missing adoption");
            if (reason is not null) issues.Add(reason);
            var complete = issues.Count == 0;
            windows.Add(new(scheduled.Value, stopped, firstTick, adopted, complete ? "complete" : "incomplete", issues,
                Duration(scheduled, stopped), Duration(stopped, firstTick), Duration(firstTick, adopted),
                complete ? Duration(scheduled, adopted) : null));
            scheduled = stopped = firstTick = null;
        }

        foreach (var item in orderedEvents)
        {
            var kind = OwnerDigestMeasurementLogs.String(item.Data, "eventKind");
            var detail = OwnerDigestMeasurementLogs.String(item.Data, "detail") ?? "";
            var head = detail.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (kind == "loop-relaunch" && head == "LOOP_RELAUNCH_SCHEDULED")
            {
                if (scheduled is not null && stopped is null) continue; // Repeated schedule, same attempt.
                Close(null, "superseded by schedule");
                scheduled = item.Timestamp;
            }
            else if (scheduled is not null && kind == "loop-stop" && stopped is null &&
                detail.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Contains("reason=self-relaunch-handoff"))
                stopped = item.Timestamp;
            else if (scheduled is not null && stopped is not null && firstTick is null && kind == "loop-first-tick" &&
                item.Timestamp > stopped && IsReplacement(item.Data, stopped.Value))
                firstTick = item.Timestamp;
            else if (scheduled is not null && kind == "loop-handoff" && head == "ACTIVATION_ADOPTED" &&
                stopped is not null && item.Timestamp > (firstTick ?? stopped))
                Close(item.Timestamp);
            else if (scheduled is not null && kind == "loop-handoff" &&
                head is "ACTIVATION_REVERTED" or "LOOP_HANDOFF_FAILED")
                Close(null, head);
        }
        Close(null);
        return windows;
    }

    private static bool IsReplacement(JsonElement record, DateTimeOffset stop) =>
        !record.TryGetProperty("process_started", out var value) || value.ValueKind == JsonValueKind.Null ||
        (value.ValueKind == JsonValueKind.String && (!value.TryGetDateTimeOffset(out var start) || start >= stop - ProcessStartTolerance));

    private static double? Duration(DateTimeOffset? start, DateTimeOffset? end) =>
        start is not null && end is not null && end >= start ? (end.Value - start.Value).TotalMilliseconds : null;
}
