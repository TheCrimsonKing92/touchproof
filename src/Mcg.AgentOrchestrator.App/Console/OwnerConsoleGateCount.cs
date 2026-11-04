namespace Mcg.AgentOrchestrator.App.OwnerConsole;

// Logical work for presentation: lanes and checks, excluding additional attempts and comparisons.
internal sealed record OwnerConsoleGateCount(int Done, int Total, int Failed, int Running,
    int Passed, int Cached, int Queued, int Unknown,
    IReadOnlyList<OwnerConsoleGateLane> FailedItems, bool Confirming)
{
    internal static OwnerConsoleGateCount Of(OwnerConsoleGateProgress gate)
    {
        // Older snapshots can carry only aggregate progress, with no lane observations.
        if (gate.Lanes.Count == 0)
            return new(gate.DoneCount, gate.TotalCount, 0, 0, 0, 0, 0, 0, [], false);

        var observed = OwnerConsoleGateConfirmation.Attach(gate.Lanes);
        var linked = observed.Select(row => row.Confirmation?.SourceName).OfType<string>()
            .ToHashSet(StringComparer.Ordinal);
        var items = observed.Where(row => !row.IsAttribution &&
            !row.Name.EndsWith(" [trusted main baseline]", StringComparison.Ordinal) &&
            !linked.Contains(row.Name) && row.State != OwnerConsoleGateLaneState.Skipped).ToArray();
        var failed = items.Where(row => row.State == OwnerConsoleGateLaneState.Done && row.Outcome == "failed")
            .OrderBy(row => row.IsManifestLane ? 0 : 1).ToArray();
        // Failure visibility includes auxiliary checks; scheduled arithmetic does
        // not. Null supports older hand-authored snapshots, not reconstructed plans.
        if (gate.LanePlanAvailable is not null) items = observed.Where(row => row.IsScheduled == true).ToArray();
        return new(
            items.Count(row => row.State is OwnerConsoleGateLaneState.Done or OwnerConsoleGateLaneState.Cached),
            items.Length, items.Count(row => row.State == OwnerConsoleGateLaneState.Done && row.Outcome == "failed"),
            items.Count(row => row.State == OwnerConsoleGateLaneState.Running),
            items.Count(row => row.State == OwnerConsoleGateLaneState.Done && row.Outcome == "passed"),
            items.Count(row => row.State == OwnerConsoleGateLaneState.Cached),
            items.Count(row => row.State == OwnerConsoleGateLaneState.Queued),
            items.Count(row => row.State == OwnerConsoleGateLaneState.Done && row.Outcome is not ("passed" or "failed")),
            failed, failed.Any(row => row.Confirmation?.Running == true));
    }
}
