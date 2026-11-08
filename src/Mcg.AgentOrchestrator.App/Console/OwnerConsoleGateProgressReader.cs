using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.Infrastructure;
using static Mcg.AgentOrchestrator.App.OwnerConsole.OwnerConsoleGateProgress;
using static Mcg.AgentOrchestrator.App.OwnerConsole.OwnerConsoleGateExecutionProjection;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal static class OwnerConsoleGateProgressReader
{
    private static readonly Regex AttemptId = new("^" + AttemptPattern + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex Fields = new(
        """(?:^|\s)(?<key>[\w]+)=(?<value>"(?:\\.|[^"\\])*"|[^\s"]+)""",
        RegexOptions.CultureInvariant);

    internal static OwnerConsoleGateProgress Build(DateTimeOffset now, IEnumerable<OwnerConductEvent> events,
        IReadOnlyList<OwnerConsoleGateManifestLane> manifest, IEnumerable<OwnerConsoleGateDuration> durations,
        Func<string, string?>? title = null, IReadOnlyDictionary<string, double>? quietSeconds = null,
        OwnerConsoleGateLaneResolver? lanes = null, int? capacity = null,
        IReadOnlyList<OwnerConsoleGateExecutorSlots>? executors = null,
        OwnerConsoleGateLanePlanReader? planReader = null)
    {
        var parsed = events.OrderBy(item => item.Timestamp).Select((item, order) => new Observation(item, Tokens(item.Detail), order)).ToArray();
        var breakdownConcurrency = BreakdownConcurrency(parsed) ?? Math.Max(1, capacity ?? 1);
        var executorSlots = executors?.Where(executor => executor.Enabled).Sum(executor => Math.Max(0, executor.Slots)) ?? 0;
        var gates = Reconstruct(parsed, out var ejections, out var lastEnd);
        var history = durations.Where(item => double.IsFinite(item.DurationSeconds) && item.DurationSeconds > 0).ToArray();
        lanes ??= new OwnerConsoleGateLaneResolver(manifest);
        var snapshots = gates.Values.Select(gate => Snapshot(gate, parsed, now, manifest, history, title, quietSeconds, lanes, planReader, breakdownConcurrency, executorSlots)).ToArray();
        var live = snapshots.Where(gate => gate.Live).OrderBy(gate => gate.StartedAt).ToArray();
        var views = MemberProjections(snapshots, ejections, now);
        var primary = live.FirstOrDefault() ?? Idle(lastEnd is null ? null : Nonnegative(now - lastEnd.Value));
        return primary with { OtherLiveGates = live.Skip(1).ToArray(), MemberViews = views, Executions = snapshots };
    }

    private static OwnerConsoleGateProgress Snapshot(Gate gate, Observation[] parsed, DateTimeOffset now,
        IReadOnlyList<OwnerConsoleGateManifestLane> manifest, OwnerConsoleGateDuration[] history,
        Func<string, string?>? title, IReadOnlyDictionary<string, double>? quietSeconds,
        OwnerConsoleGateLaneResolver lanes, OwnerConsoleGateLanePlanReader? planReader,
        int breakdownConcurrency, int executorSlots)
    {
        var (currentAttempt, plan, skipped) = ReadPlan(gate, planReader);
        // Identified executions accept only their own remote receipts. Legacy
        // records without identity cannot fill gaps in an identified attempt.
        var remote = parsed.Where(item => item.Event.EventKind == "remote-lane" &&
            (currentAttempt.Length > 0 ? item.Get("attempt").Equals(currentAttempt, StringComparison.OrdinalIgnoreCase) :
                !AttemptId.IsMatch(item.Get("attempt")) && item.Event.Timestamp >= gate.Start &&
                gate.Members.Any(id => Prefix(id) == Prefix(item.Event.GoalId ?? "")))).ToArray();
        var attribution = AttributionObservations(parsed, gate).ToArray();
        var lastObserved = remote.Concat(attribution).Select(item => item.Event.Timestamp).Append(gate.Last).Max();
        var resolver = plan is null ? lanes : new OwnerConsoleGateLaneResolver(
            plan.Scheduled.Concat(plan.Skipped.Select(row => row.Name))
                .Select(name => new OwnerConsoleGateManifestLane(name, 0)).ToArray());
        string Resolve(string target) => resolver.Resolve(target);
        var samplesByLane = history.GroupBy(item => Resolve(item.LaneName), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(item => item.RecordedAt)
                .Take(10).Select(item => item.DurationSeconds).Order().ToArray(), StringComparer.Ordinal);
        var laneEvents = gate.Events.Where(item => IsLanePhase(item.Get("phase")))
            .Select(item => (Item: item, Target: Resolve(item.Get("target")))).ToArray();
        var remoteEvents = remote.Select(item => (Item: item, Target: Resolve(item.Get("lane")))).ToArray();
        var observationsByLane = laneEvents.ToLookup(item => item.Target, item => item.Item, StringComparer.Ordinal);
        var remoteByLane = remoteEvents.ToLookup(item => item.Target, item => item.Item, StringComparer.Ordinal);
        var names = LaneNames(plan, laneEvents, remoteEvents);
        var rows = names.Select(name => OwnerConsoleGateLaneRowBuilder.Row(name, manifest, samplesByLane, skipped,
            observationsByLane, remoteByLane, plan, now, quietSeconds)).Concat(AttributionRows(attribution, gate)).ToArray();
        var (counted, done, remaining, gateElapsed, fraction, etaAvailable) =
            OwnerConsoleGateProgressCalculation.Estimate(rows, breakdownConcurrency, executorSlots, plan, now, gate);
        var kind = gate.TrainIdentity is not null || gate.Events.Any(item => item.Get("train").Length > 0 || item.Get("scope") == "merge-train") ? "train" :
            gate.Members.Length > 1 ? "cohort" : "solo";
        return new(gate.Live && now - lastObserved <= TimeSpan.FromMinutes(15), kind, gate.Members.Select(id => new OwnerConsoleGateMember(id, title?.Invoke(id) ?? "")).ToArray(),
            gate.Start, gateElapsed, rows, fraction,
            (int)remaining, done.Length, counted.Length,
            LanePlanAvailable: plan is not null, ExecutionId: gate.ExecutionId, OuterAttemptId: gate.OuterAttemptId,
            TrainIdentity: gate.TrainIdentity, Participation: gate.Participation, EtaAvailable: etaAvailable,
            AdmittedAt: gate.AdmittedAt, FinalOutcome: gate.FinalOutcome, FinishedAt: gate.FinishedAt,
            FinishedAge: gate.FinishedAt is { } finished ? Nonnegative(now - finished) : null);
    }

    private static (string currentAttempt, AcceptanceGateLanePlan? plan, Dictionary<string, string>? skipped)
        ReadPlan(Gate gate, OwnerConsoleGateLanePlanReader? planReader)
    {
        var currentAttempt = gate.ExecutionId ?? "";
        var planEvent = gate.Events.LastOrDefault(item => item.Get("phase") == "lane-plan" &&
            ExecutionOf(item).Equals(currentAttempt, StringComparison.OrdinalIgnoreCase));
        var planPath = planEvent is not null ? planEvent.Get("heartbeat") : gate.Events
            .Select(item => item.Get("heartbeat"))
            // Conductor attempts use a slot index, while standalone attempts use
            // "gate". The recorded execution owns the artifact; do not reparse it.
            .Where(path => currentAttempt.Length > 0 && path.Split(['/', '\\']).Last()
                .StartsWith(currentAttempt + ".", StringComparison.OrdinalIgnoreCase))
            .Select(path => OwnerConsoleGateLanePlanReader.ArtifactPath(path, currentAttempt))
            .LastOrDefault(path => path is not null);
        var plan = planPath is null ? null : planReader?.Read(planPath, currentAttempt);
        var skipped = plan?.Skipped.ToDictionary(row => row.Name, row => row.Reason, StringComparer.Ordinal);
        return (currentAttempt, plan, skipped);
    }

    private static List<string> LaneNames(AcceptanceGateLanePlan? plan,
        (Observation Item, string Target)[] laneEvents, (Observation Item, string Target)[] remoteEvents)
    {
        var names = plan?.Scheduled.Concat(plan.Skipped.Select(row => row.Name)).ToList() ?? [];
        foreach (var item in laneEvents)
        {
            var target = item.Target;
            if (target.Length > 0 && !names.Contains(target)) names.Add(target);
        }
        foreach (var item in remoteEvents)
        {
            if (item.Item.Get("decision") != "offer" && item.Item.Get("outcome") is not ("accepted" or "completed" or "offer-skipped-no-executor")) continue;
            var target = item.Target;
            if (target.Length > 0 && !names.Contains(target)) names.Add(target);
        }
        return names;
    }

    internal static OwnerConductEvent? ParseEvent(string line)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            return new(root.GetProperty("timestamp").GetDateTimeOffset(), root.GetProperty("eventKind").GetString() ?? "",
                root.TryGetProperty("goalId", out var goal) ? goal.GetString() : null, root.GetProperty("detail").GetString() ?? "");
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException) { return null; }
    }

    private static bool IsLanePhase(string phase) => phase is "verification-check" or "shard-complete" or "remote-shard-complete" or "check-complete" or "remote-shard-started" or "cache-hit" or "reuse";

    private static int? BreakdownConcurrency(Observation[] parsed)
    {
        var newest = parsed.LastOrDefault(item => item.Event.Detail.StartsWith("PHASE_PROGRESS ", StringComparison.Ordinal) &&
            item.Get("phase") == "gate-phase-breakdown");
        foreach (var field in (newest?.Get("target") ?? "").Split(';'))
        {
            var separator = field.IndexOf('=');
            if (separator < 0 || !field[..separator].Equals("shard_concurrency_effective", StringComparison.Ordinal)) continue;
            return int.TryParse(field[(separator + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) &&
                value >= 1 ? value : null;
        }
        return null;
    }

    private static Dictionary<string, string> Tokens(string detail) => Fields.Matches(detail).Cast<Match>()
        .GroupBy(match => match.Groups["key"].Value).ToDictionary(group => group.Key, group =>
        {
            var value = group.Last().Groups["value"].Value;
            return value.StartsWith('"') ? value[1..^1].Replace("\\\"", "\"", StringComparison.Ordinal) : value;
        }, StringComparer.Ordinal);
}
