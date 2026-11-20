namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed record RemoteExecutorOccupancyEntry(string ExecutorId, int Gate, int FocusedEvidence,
    int Shadow, int Screening, int Unclassified, string State);

// An advisory snapshot for diagnostics and offer sizing. Claiming owns allocation.
internal static class RemoteExecutorOccupancyCensus
{
    internal static IReadOnlyList<RemoteExecutorOccupancyEntry> Take(string root,
        IEnumerable<(string ExecutorId, string State)> executors, Func<int, DateTimeOffset?>? startTimeOf = null) =>
        executors.Select(executor => Count(executor.ExecutorId, executor.State,
            RemoteExecutorOccupancy.LiveMarkers(root, executor.ExecutorId, startTimeOf))).ToArray();

    internal static IReadOnlyList<int> TakeGateFreeSlots(string root,
        IEnumerable<(string ExecutorId, int ConfiguredSlots)> executors, Func<int, DateTimeOffset?>? startTimeOf = null) =>
        executors.Select(executor =>
        {
            try
            {
                var markers = RemoteExecutorOccupancy.LiveMarkers(root, executor.ExecutorId, startTimeOf,
                    requireReadableDirectory: true);
                var counts = Count(executor.ExecutorId, "", markers);
                // Gate claims can preempt or outrank shadow work.
                return Math.Max(0, executor.ConfiguredSlots - counts.Gate - counts.FocusedEvidence -
                    counts.Screening - counts.Unclassified);
            }
            catch (Exception) { return 0; }
        }).ToArray();

    private static RemoteExecutorOccupancyEntry Count(string executorId, string state,
        IReadOnlyList<(string Attempt, string? Class)> markers)
    {
        var counts = new int[5];
        foreach (var marker in markers)
        {
            var index = Enum.TryParse<RemoteExecutorClaimClass>(marker.Class, true, out var claimClass) &&
                Enum.IsDefined(claimClass) && string.Equals(marker.Class, claimClass.ToString(), StringComparison.OrdinalIgnoreCase) ? (int)claimClass :
                marker.Attempt.StartsWith("screening-", StringComparison.Ordinal) ? 3 : 4;
            counts[index]++;
        }
        return new(executorId, counts[0], counts[1], counts[2], counts[3], counts[4], state);
    }

    internal static string Reason(IReadOnlyList<RemoteExecutorOccupancyEntry> entries) =>
        "no-idle-slot" + string.Concat(entries.Select(entry =>
            $" {entry.ExecutorId}=gate:{entry.Gate},focused-evidence:{entry.FocusedEvidence},shadow:{entry.Shadow}," +
            $"screening:{entry.Screening},unclassified:{entry.Unclassified},state:{entry.State}"));
}
