using System.Globalization;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal sealed class OwnerConsoleGateFieldProjection(OwnerConsoleGateProgress gate)
{
    internal OwnerConsoleFieldSnapshot FieldSnapshot()
    {
        var counts = OwnerConsoleGateCount.Of(gate);
        var fields = new List<OwnerConsoleField> {
        new("kind", "Kind", gate.Kind), new("started", "Started at", gate.StartedAt?.ToString("O")),
        new("elapsed", "Elapsed", gate.Elapsed.ToString("c")), new("live", "Live", gate.Live.ToString()),
        new("done", "Done", counts.Done.ToString(CultureInfo.InvariantCulture)),
        new("total", "Total", gate.ScheduleKnown ? counts.Total.ToString(CultureInfo.InvariantCulture) : null),
        new("remaining", "Remaining seconds", gate.HasEta ? gate.RemainingSeconds.ToString(CultureInfo.InvariantCulture) : null),
        new("execution", "Execution ID", gate.ExecutionId), new("outer-attempt", "Outer attempt ID", gate.OuterAttemptId),
        new("train", "Train identity", gate.TrainIdentity), new("participation", "Participation", gate.Participation),
        new("progress", "Progress fraction", gate.Fraction.ToString("R", CultureInfo.InvariantCulture)),
        new("idle-age", "Idle age", gate.IdleAge?.ToString("c")),
        new("failed", "Failed", counts.Failed.ToString(CultureInfo.InvariantCulture)),
        new("running", "Running", counts.Running.ToString(CultureInfo.InvariantCulture)),
        new("passed", "Passed", counts.Passed.ToString(CultureInfo.InvariantCulture)),
        new("cached", "Cached", counts.Cached.ToString(CultureInfo.InvariantCulture)),
        new("queued", "Queued", counts.Queued.ToString(CultureInfo.InvariantCulture)),
        new("skipped", "Skipped", gate.Lanes.Count(lane => !lane.IsAttribution && lane.State == OwnerConsoleGateLaneState.Skipped &&
            !lane.Name.EndsWith(" [trusted main baseline]", StringComparison.Ordinal)).ToString(CultureInfo.InvariantCulture)),
        new("unknown", "Unknown", counts.Unknown.ToString(CultureInfo.InvariantCulture)) };
        foreach (var member in gate.Members)
            fields.AddRange(MemberFields(member).Fields.Select(field => field with { Key = "member/" + member.Id + "/" + field.Key }));
        return new("gate/" + gate.StartedAt?.ToString("O"), [.. fields]);
    }

    internal static OwnerConsoleFieldSnapshot LaneFields(OwnerConsoleGateLane row) => OwnerConsoleFieldSnapshot.Create("lane/" + row.Name,
        new("name", "Lane", row.Name), new("state", "State", row.State.ToString()), new("outcome", "Outcome", row.Outcome),
        new("location", "Location", row.Location), new("elapsed", "Elapsed", row.Elapsed.ToString("c")),
        new("median", "Median seconds", row.MedianSeconds?.ToString("R", CultureInfo.InvariantCulture)),
        new("quiet", "Quiet seconds", row.QuietSeconds?.ToString("R", CultureInfo.InvariantCulture)),
        new("reproduction-member", "Reproduction member ID", row.ReproductionMemberId),
        new("target", "Target", row.Target), new("heartbeat", "Heartbeat path", row.HeartbeatPath),
        new("failing", "Failing tests", row.FailingTestNames is null ? null : string.Join("\n", row.FailingTestNames)),
        new("skip", "Skip reason", row.SkipReason), new("tree", "Tree revision", row.TreeRevision),
        new("skipped-count", "Skipped test count", row.SkippedTestCount.ToString(CultureInfo.InvariantCulture)),
        new("evidence", "Evidence kind", row.EvidenceKind), new("confirmation", "Confirmation", row.Confirmation?.Line),
        new("reproductions", "Reproductions", row.Reproductions is null ? null : string.Join("\n", row.Reproductions.Select(item => item.Line))),
        new("reproduction-result", "Reproduction result", row.ReproductionResult));

    internal static OwnerConsoleFieldSnapshot MemberFields(OwnerConsoleGateMember member) => OwnerConsoleFieldSnapshot.Create("member/" + member.Id,
        new("goal", "Goal ID", member.Id.Length > 8 ? member.Id : null),
        new("prefix", "Observed goal reference", member.Id), new("title", "Title", member.Title));
    internal OwnerConsoleFieldSnapshot CapacityFields()
    {
        var observed = OwnerConsoleGateConfirmation.Attach(gate.Lanes);
        var linked = observed.Select(row => row.Confirmation?.SourceName).OfType<string>().ToHashSet(StringComparer.Ordinal);
        var running = observed.Where(row => !row.IsAttribution && !linked.Contains(row.Name) &&
            !row.Name.EndsWith(" [trusted main baseline]", StringComparison.Ordinal) && row.State == OwnerConsoleGateLaneState.Running).ToArray();
        var fields = new List<OwnerConsoleField> {
            new("local", "Local budget", gate.Capacity?.LocalBudget.ToString(CultureInfo.InvariantCulture)),
            new("local/running", "Local running lanes", running.Count(row => OwnerConsoleGateWhereLine.IsLocal(row.Location)).ToString(CultureInfo.InvariantCulture)) };
        foreach (var executor in gate.Capacity?.Executors ?? []) fields.AddRange(new OwnerConsoleField[] {
            new(executor.Id + "/id", "Executor ID", executor.Id), new(executor.Id + "/slots", "Slots", executor.Slots.ToString(CultureInfo.InvariantCulture)),
            new(executor.Id + "/enabled", "Enabled", executor.Enabled.ToString()),
            new(executor.Id + "/running", "Running lanes", running.Count(lane => lane.Location == executor.Id).ToString(CultureInfo.InvariantCulture)) });
        foreach (var group in gate.Lanes.Where(lane => !lane.IsAttribution && lane.Location.Length > 0).GroupBy(lane => lane.Location))
            fields.AddRange(new OwnerConsoleField[] { new("location/" + group.Key, "Location", group.Key),
                new("location/" + group.Key + "/running", "Running lanes", group.Count(lane => lane.State == OwnerConsoleGateLaneState.Running).ToString(CultureInfo.InvariantCulture)),
                new("location/" + group.Key + "/owner", "Owner", gate.Capacity?.Owners?.ForRunningLanes(group.Where(lane => lane.State == OwnerConsoleGateLaneState.Running))) });
        return new("capacity", [.. fields]);
    }
}
