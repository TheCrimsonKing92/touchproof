using System.Text.RegularExpressions;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal enum OwnerConsoleGateLaneState { Planned, Queued, Running, Done, Cached, Skipped }
internal sealed record OwnerConsoleGateMember(string Id, string Title);
internal sealed record OwnerConsoleGateLane(string Name, OwnerConsoleGateLaneState State,
    string Outcome, string Location, TimeSpan Elapsed, double? MedianSeconds, bool IsEstimate,
    double? QuietSeconds = null, bool IsManifestLane = true,
    string? Target = null, string? HeartbeatPath = null, bool IsAttribution = false,
    IReadOnlyList<string>? FailingTestNames = null, int SkippedTestCount = 0,
    OwnerConsoleGateConfirmation? Confirmation = null, string? SkipReason = null,
    string? TreeRevision = null, string? EvidenceKind = null, string? ReproductionMemberId = null,
    IReadOnlyList<OwnerConsoleGateReproduction>? Reproductions = null, string? ReproductionResult = null,
    bool? IsScheduled = null);
internal sealed record OwnerConsoleGateManifestLane(string Name, double EstimatedSerialSeconds);
internal sealed record OwnerConsoleGateDuration(string LaneName, double DurationSeconds, DateTimeOffset RecordedAt);

// A snapshot of observation, never an authority for scheduling or acceptance.
internal sealed record OwnerConsoleGateProgress(bool Live, string Kind,
    IReadOnlyList<OwnerConsoleGateMember> Members, DateTimeOffset? StartedAt, TimeSpan Elapsed,
    IReadOnlyList<OwnerConsoleGateLane> Lanes, double Fraction, int RemainingSeconds,
    int DoneCount, int TotalCount, TimeSpan? IdleAge = null,
    IReadOnlyList<OwnerConsoleGateProgress>? OtherLiveGates = null,
    IReadOnlyList<OwnerConsoleHostActivity>? HostActivity = null,
    OwnerConsoleGateCapacity? Capacity = null,
    IReadOnlyList<OwnerConsoleGateEnd>? Ended = null,
    IReadOnlyList<OwnerConsoleEvidenceAttemptEnd>? EvidenceEnded = null,
    bool? LanePlanAvailable = null,
    string? ExecutionId = null, string? OuterAttemptId = null, string? TrainIdentity = null,
    string? Participation = null,
    IReadOnlyDictionary<string, OwnerConsoleGateProgress>? MemberViews = null,
    IReadOnlyList<OwnerConsoleGateProgress>? Executions = null,
    bool? EtaAvailable = null, DateTimeOffset? AdmittedAt = null,
    IReadOnlyList<OwnerConsoleGateProgress>? ConcurrentRuns = null,
    string? FinalOutcome = null, DateTimeOffset? FinishedAt = null, TimeSpan? FinishedAge = null)
{
    internal bool ScheduleKnown => LanePlanAvailable != false;
    internal bool HasEta => EtaAvailable ?? ScheduleKnown;
    internal bool Failing => Lanes.Any(lane => !lane.IsAttribution &&
        lane.State == OwnerConsoleGateLaneState.Done && lane.Outcome == "failed");
    internal const string AttemptPattern = @"[0-9a-f]{8}-gate-[0-9]{17}-[0-9a-f]{32}";
    private static readonly Regex HeartbeatAttempt = new("^" + AttemptPattern + @"(?=\.|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    internal static OwnerConsoleGateProgress Idle(TimeSpan? age = null) =>
        new(false, "idle", [], null, TimeSpan.Zero, [], 0, 0, 0, 0, age);

    internal OwnerConsoleGateProgress? ForMember(string id)
    {
        if (MemberViews is null) return Members.Any(member => MemberMatches(member.Id, id)) ? this :
            OtherLiveGates?.FirstOrDefault(gate => gate.Members.Any(member => MemberMatches(member.Id, id)));
        if (MemberViews.TryGetValue(id, out var exact)) return exact;
        var matches = MemberViews.Where(pair => MemberMatches(pair.Key, id)).Select(pair => pair.Value).Distinct().ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    internal OwnerConsoleGateProgress? ForExecution(string id) => ExecutionId == id ? this :
        Executions?.FirstOrDefault(gate => gate.ExecutionId == id);

    internal static OwnerConsoleGateProgress Build(DateTimeOffset now, IEnumerable<OwnerConductEvent> events,
        IReadOnlyList<OwnerConsoleGateManifestLane> manifest, IEnumerable<OwnerConsoleGateDuration> durations,
        Func<string, string?>? title = null, IReadOnlyDictionary<string, double>? quietSeconds = null,
        OwnerConsoleGateLaneResolver? lanes = null, int? capacity = null,
        IReadOnlyList<OwnerConsoleGateExecutorSlots>? executors = null,
        OwnerConsoleGateLanePlanReader? planReader = null) =>
        OwnerConsoleGateProgressReader.Build(now, events, manifest, durations, title, quietSeconds, lanes, capacity, executors, planReader);

    internal static OwnerConductEvent? ParseEvent(string line) => OwnerConsoleGateProgressReader.ParseEvent(line);

    internal static string AttemptFromHeartbeat(string path) => HeartbeatAttempt.Match(path.Split(['/', '\\']).Last()).Value;
    internal static TimeSpan Nonnegative(TimeSpan value) => value < TimeSpan.Zero ? TimeSpan.Zero : value;
    internal static string Prefix(string id) => id.Trim()[..Math.Min(8, id.Trim().Length)];
    internal static bool MemberMatches(string left, string right) => left.Length > 8 && right.Length > 8
        ? left == right : Prefix(left) == Prefix(right);
}
