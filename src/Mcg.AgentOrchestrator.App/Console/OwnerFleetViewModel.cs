using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal enum OwnerFleetReachability { Unknown, Reachable, Unreachable }

internal sealed record OwnerFleetHostNode(bool ConductorRunning, int ExecutorCount,
    int UnreadableOutcomeLines, int UnreadableProbeLines,
    int NoExecutorFreeCount = 0, DateTimeOffset? NoExecutorFreeLastAt = null)
{
    internal OwnerConsoleFieldSnapshot Fields() => OwnerConsoleFieldSnapshot.Create("host",
        new("conductor", "Conductor running", ConductorRunning.ToString()), new("executors", "Executor count", ExecutorCount.ToString()),
        new("unreadable-outcomes", "Unreadable outcomes", UnreadableOutcomeLines.ToString()),
        new("unreadable-probes", "Unreadable probes", UnreadableProbeLines.ToString()),
        new("no-free", "No free slot count", NoExecutorFreeCount.ToString()), new("last-no-free", "Last no free slot", NoExecutorFreeLastAt?.ToString("O")));
}

internal sealed record OwnerFleetExecutorNode(string Id, OwnerFleetReachability Reachability,
    IReadOnlyList<string> ProbeReasons, DateTimeOffset? ProbeAt, int? ConfiguredSlots,
    int PeakConcurrentAttempts, string? LastOutcome, DateTimeOffset? LastOutcomeAt,
    double? AcceptedShare, IReadOnlyList<RemoteExecutorLaneSeconds> Lanes, RemoteExecutorSlotsSyncRow? SlotsFile = null,
    bool? AdmissionEnabled = null, string? Owner = null)
{
    internal OwnerConsoleFieldSnapshot Fields()
    {
        var fields = new List<OwnerConsoleField>
        {
            new("executor", "Executor ID", Id), new("reachability", "Reachability", Reachability.ToString()),
            new("reasons", "Probe reasons", string.Join("\n", ProbeReasons)), new("probe", "Probe at", ProbeAt?.ToString("O")),
            new("slots", "Configured slots", ConfiguredSlots?.ToString()), new("peak", "Peak concurrency", PeakConcurrentAttempts.ToString()),
            new("outcome", "Last outcome", LastOutcome), new("outcome-at", "Last outcome at", LastOutcomeAt?.ToString("O")),
            new("share", "Accepted share", AcceptedShare?.ToString("R", System.Globalization.CultureInfo.InvariantCulture)),
            new("admission", "Admission enabled", AdmissionEnabled?.ToString()), new("owner", "Owner", Owner),
            new("sync", "Slots file outcome", SlotsFile?.Outcome), new("sync-reason", "Slots file reason", SlotsFile?.Reason),
            new("sync-slots", "Slots file slots", SlotsFile?.Slots.ToString()),
            new("sync-at", "Slots file observed at", SlotsFile?.ObservedAt.ToString("O"))
        };
        foreach (var lane in Lanes)
        {
            var key = "lane/" + lane.Lane + "/";
            fields.AddRange(new OwnerConsoleField[] { new(key + "name", "Lane", lane.Lane),
                new(key + "median", lane.Lane + " median seconds", lane.MedianSeconds.ToString()),
                new(key + "samples", lane.Lane + " samples", lane.Samples.ToString()),
                new(key + "solo", lane.Lane + " solo median seconds", lane.SoloMedianSeconds?.ToString("R", System.Globalization.CultureInfo.InvariantCulture)),
                new(key + "concurrent", lane.Lane + " concurrent median seconds", lane.ConcurrentMedianSeconds?.ToString("R", System.Globalization.CultureInfo.InvariantCulture)) });
        }
        return new(Id, [.. fields]);
    }
}

internal sealed record OwnerFleetViewModel(OwnerFleetHostNode Host,
    IReadOnlyList<OwnerFleetExecutorNode> Executors, IReadOnlyList<OwnerFleetProviderHoldNode>? ProviderHolds = null);

internal sealed record OwnerFleetProviderHoldNode(string BindingKey, string Name, string Source,
    DateTimeOffset PlacedAt, DateTimeOffset? NextProbeAt, int? Attempt, string? ResetSource, string? LastProbe)
{
    internal string? SourceGoalId { get; init; }
    internal string? SourceTaskId { get; init; }
    internal DateTimeOffset? OverdueSince { get; init; }
    internal DateTimeOffset? LastObservedAt { get; init; }
    internal OwnerConsoleFieldSnapshot Fields() => OwnerConsoleFieldSnapshot.Create(BindingKey,
        new("binding", "Binding", BindingKey), new("name", "Name", Name), new("source-goal", "Source goal ID", SourceGoalId),
        new("source-task", "Source task ID", SourceTaskId), new("placed", "Placed at", PlacedAt.ToString("O")),
        new("next", "Next probe", NextProbeAt?.ToString("O")), new("attempt", "Attempt", Attempt?.ToString()),
        new("reset", "Reset source", ResetSource), new("last", "Last probe", LastProbe));
}

internal static class OwnerFleetViewModelBuilder
{
    internal static OwnerFleetViewModel Build(RemoteExecutorReport report, bool conductorRunning, TimeZoneInfo zone,
        IReadOnlyDictionary<string, RemoteExecutorSlotsSyncRow>? slotsSync = null,
        IReadOnlyList<ProviderUsageHoldRow>? providerHolds = null, OwnerConsoleHostRowOwner? owners = null) =>
        new(new(conductorRunning, report.Executors.Count, report.UnreadableOutcomeLines, report.UnreadableProbeLines,
                report.NoExecutorFreeCount, report.NoExecutorFreeLastAt is { } at ? TimeZoneInfo.ConvertTime(at, zone) : null),
            report.Executors.Select(row =>
            {
                var last = row.RecentAttempts.FirstOrDefault();
                return new OwnerFleetExecutorNode(row.ExecutorId, row.LastProbe is null ? OwnerFleetReachability.Unknown :
                    row.LastProbe.Reachable ? OwnerFleetReachability.Reachable : OwnerFleetReachability.Unreachable,
                    row.LastProbe?.Reasons.ToArray() ?? [],
                    row.LastProbe is { } probe ? TimeZoneInfo.ConvertTime(probe.ObservedAt, zone) : null,
                    row.ConfiguredSlots, row.PeakConcurrentAttempts, last?.Outcome,
                    last is null ? null : TimeZoneInfo.ConvertTime(last.ObservedAt, zone), row.AcceptedShare, row.Lanes.ToArray(),
                    slotsSync?.GetValueOrDefault(row.ExecutorId) is { } sync ?
                        sync with { ObservedAt = TimeZoneInfo.ConvertTime(sync.ObservedAt, zone) } : null, row.AdmissionEnabled,
                    owners?.ForAttempts(row.RecentAttempts.Select(attempt => attempt.GateAttemptId)));
            }).ToArray(), (providerHolds ?? []).Select(row => new OwnerFleetProviderHoldNode(
                row.Hold.BindingKey, OwnerProviderHoldNarration.DisplayName(row.Hold.BindingKey),
                $"{row.Hold.SourceGoalId.Value[..8]}/{row.Hold.SourceTaskId.Value[..8]}",
                TimeZoneInfo.ConvertTime(row.Hold.PlacedAt, zone),
                row.Schedule is { } schedule ? TimeZoneInfo.ConvertTime(schedule.NextProbeAt, zone) : null,
                row.Schedule?.Attempt, row.Schedule?.ResetSource,
                row.Schedule?.LastProbeOutcome is { } outcome && outcome != "none" ? outcome : null)
                { SourceGoalId = row.Hold.SourceGoalId.Value, SourceTaskId = row.Hold.SourceTaskId.Value,
                  OverdueSince = row.OverdueSince is { } due ? TimeZoneInfo.ConvertTime(due, zone) : null,
                  LastObservedAt = row.LastObservedAt is { } observed ? TimeZoneInfo.ConvertTime(observed, zone) : null }).ToArray());

    internal static OwnerFleetViewModel Load(OrchestratorWorkspace workspace, IConductorLiveness liveness, TimeProvider? clock = null,
        OwnerConsoleHostRowOwner? owners = null) =>
        Build(RemoteExecutorReportReader.Read(workspace, null, 10, (clock ?? TimeProvider.System).GetUtcNow().AddHours(-24)),
            liveness.IsRunning(), TimeZoneInfo.Local, RemoteExecutorSlotsSyncLedger.ReadLatestByExecutor(
                Path.Combine(Path.GetDirectoryName(RemoteLaneExecutorConfiguration.ResolveStorePath(workspace.ExecutionDirectory))!,
                    RemoteExecutorSlotsSyncLedger.FileName)),
            ProviderUsageHoldReader.ReadWorkspace(workspace, (clock ?? TimeProvider.System).GetUtcNow()), owners);
}
