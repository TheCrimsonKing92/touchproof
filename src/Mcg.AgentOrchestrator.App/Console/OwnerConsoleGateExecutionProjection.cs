using static Mcg.AgentOrchestrator.App.OwnerConsole.OwnerConsoleGateProgress;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal static class OwnerConsoleGateExecutionProjection
{
    // Identity is a recorded relationship, never a comparison of member sets.
    internal sealed record Ejection(string? Outer, string Goal, string? Execution, DateTimeOffset At, string? Train);

    internal static string ExecutionOf(Observation item)
    {
        var execution = item.Get("execution");
        if (execution.Length == 0 && item.Event.Detail.StartsWith("ACCEPTANCE ", StringComparison.Ordinal))
            execution = item.Get("attempt");
        return execution.Length > 0 ? execution : AttemptFromHeartbeat(item.Get("heartbeat"));
    }

    internal static Dictionary<string, Gate> Reconstruct(Observation[] parsed,
        out List<Ejection> ejections, out DateTimeOffset? lastEnd)
    {
        var state = new OwnerConsoleGateReconstruction();
        // Keep legacy membership-only replay, but never turn cohort ownership/
        // admission diagnostics into an independent execution when the stream
        // carries execution identities. Members are not an identity link.
        var hasExecutionEvidence = parsed.Any(item =>
            (item.Event.EventKind == "gate-progress" || item.Event.Detail.StartsWith("ACCEPTANCE ", StringComparison.Ordinal) ||
             item.Event.Detail.StartsWith("ACCEPTANCE_COHORT", StringComparison.Ordinal)) &&
            item.Get("scope") != "attribution" && item.Get("phase") != "gate-phase-breakdown" && ExecutionOf(item).Length > 0);
        foreach (var item in parsed) ApplyObservation(state, item, hasExecutionEvidence);
        AddOwnerOnlyGates(state);
        ejections = state.Ejections;
        lastEnd = state.LastEnd;
        return state.Gates;
    }

    private static void ApplyObservation(OwnerConsoleGateReconstruction state, Observation item, bool hasExecutionEvidence)
    {
        var detail = item.Event.Detail;
        var cohort = detail.StartsWith("ACCEPTANCE_COHORT", StringComparison.Ordinal);
        var solo = detail.StartsWith("ACCEPTANCE ", StringComparison.Ordinal);
        if (!cohort && !solo && item.Event.EventKind != "gate-progress") return;
        // Partition run IDs describe auxiliary reproduction, never admission
        // to a new gate. Only an explicit parent link can attach their activity.
        if (item.Get("scope") == "attribution") return;
        var execution = ExecutionOf(item);
        var outer = item.Get("outer_attempt");
        if (cohort && outer.Length == 0) outer = item.Get("attempt");
        var train = item.Get("train_identity");
        var phase = item.Get("phase");
        if (phase == "gate-phase-breakdown") return;
        var members = (item.Get("participant_ids") is { Length: > 0 } full ? full : item.Get("members"))
            .Split([',', '+'], StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.Ordinal).ToArray();
        var goal = item.Get("goal");
        if (goal.Length == 0) goal = item.Event.GoalId ?? "";
        var legacyEjection = System.Text.RegularExpressions.Regex.Match(detail, @"ejected from train (?<train>[^\s"";]+)");
        if (phase == "member-ejected" || legacyEjection.Success)
        {
            ApplyEjection(state, item, goal, outer, execution, train, legacyEjection);
            return;
        }
        var outcome = item.Get("outcome");
        var terminal = cohort && (detail.StartsWith("ACCEPTANCE_COHORT_CHILD_COMPLETED", StringComparison.Ordinal) ||
            detail.StartsWith("ACCEPTANCE_COHORT_RECONCILED_DEAD", StringComparison.Ordinal) ||
            detail.StartsWith("ACCEPTANCE_COHORT_ADOPTION_REFUSED", StringComparison.Ordinal) || detail.Contains("ABANDON", StringComparison.Ordinal) ||
            (detail.StartsWith("ACCEPTANCE_COHORT_EXIT ", StringComparison.Ordinal) || detail.StartsWith("ACCEPTANCE_COHORT ", StringComparison.Ordinal)) &&
            outcome is not ("" or "inflight" or "gathering" or "unpaired")) || solo && item.Get("result") is not ("" or "started" or "running");
        if (cohort && outer.Length > 0)
        {
            ApplyOuterOwnership(state, item, outer, terminal);
            return; // Outer ownership never asserts execution participation.
        }
        if (cohort && (members.Length == 0 || execution.Length == 0 && hasExecutionEvidence)) return;
        if (members.Length == 0 && goal.Length > 0 && goal != "unknown") members = [goal];
        if (members.Length == 0) return;
        var gate = state.Admit(item, members, execution, outer, train, phase, detail, solo);
        if (state.ApplyCompletion(gate, item, outer, terminal)) return;
        state.Track(gate, item, execution, outer, phase, members);
    }

    private static void ApplyEjection(OwnerConsoleGateReconstruction state, Observation item, string goal,
        string outer, string execution, string train, System.Text.RegularExpressions.Match legacyEjection)
    {
        if (goal.Length == 0) return;
        if (train.Length == 0 && legacyEjection.Success) train = legacyEjection.Groups["train"].Value;
        // Older goal-specific receipts identify an ejection even when the
        // parent relationship was not recorded. Retain it as unresolved.
        var original = state.Gates.Values.LastOrDefault(gate => outer.Length > 0 && gate.OuterAttemptId == outer &&
            (execution.Length > 0 ? gate.ExecutionId == execution : gate.TrainIdentity == train));
        state.Ejections.Add(new(outer.Length == 0 ? null : outer, goal, original?.ExecutionId, item.Event.Timestamp, train));
        if (original is not null)
        {
            original.Live = false;
            original.Participation = "historical execution";
        }
        if (original is null)
            state.Gates["ejected:" + outer + ":" + goal + ":" + train] = new([goal], item.Event.Timestamp)
            {
                ExecutionId = execution.Length == 0 ? null : execution, OuterAttemptId = outer.Length == 0 ? null : outer,
                TrainIdentity = train.Length == 0 ? null : train, Live = false, Participation = "ejected / awaiting survivor",
                FinalOutcome = "ejected", FinishedAt = item.Event.Timestamp
            };
    }

    private static void ApplyOuterOwnership(OwnerConsoleGateReconstruction state, Observation item, string outer, bool terminal)
    {
        if (!state.CompletedOwners.Contains(outer)) state.Owners[outer] = item;
        if (terminal)
        {
            state.CompletedOwners.Add(outer);
            state.LastEnd = item.Event.Timestamp;
            foreach (var child in state.Gates.Values.Where(gate => gate.OuterAttemptId == outer))
            {
                child.Live = false;
                child.Participation = "outer attempt completed";
                if (child.FinishedAt is null)
                {
                    child.FinalOutcome = FinalOutcomeOf(item);
                    child.FinishedAt = item.Event.Timestamp;
                }
            }
        }
    }

    private static void AddOwnerOnlyGates(OwnerConsoleGateReconstruction state)
    {
        // A truncated tail may contain ownership without any child evidence. Keep
        // that uncertainty visible instead of manufacturing a live train.
        foreach (var (outer, owner) in state.Owners)
        {
            if (state.Gates.Values.Any(gate => gate.OuterAttemptId == outer)) continue;
            var members = owner.Get("members").Split([',', '+'], StringSplitOptions.RemoveEmptyEntries);
            if (members.Length == 0) continue;
            state.Gates["owner:" + outer] = new(members, owner.Event.Timestamp)
            {
                OuterAttemptId = outer, Live = false,
                Participation = state.CompletedOwners.Contains(outer) ? "outer attempt completed" : "awaiting execution evidence",
                FinalOutcome = state.CompletedOwners.Contains(outer) ? FinalOutcomeOf(owner) : null,
                FinishedAt = state.CompletedOwners.Contains(outer) ? owner.Event.Timestamp : null
            };
        }
    }

    internal static string? FinalOutcomeOf(Observation item)
    {
        if (item.Event.Detail.Contains("ABANDON", StringComparison.Ordinal)) return "cancelled";
        var outcome = item.Event.Detail.StartsWith("ACCEPTANCE ", StringComparison.Ordinal) ? item.Get("result") :
            item.Get("verdict") is { Length: > 0 } verdict ? verdict :
            item.Get("outcome") is { Length: > 0 } recorded ? recorded : item.Get("result");
        // Child result describes process completion; only a recorded gate outcome
        // establishes passed/failed. Unknown or unreadable results stay unknown.
        return outcome switch
        {
            "passed" or "gate-passed" => "passed",
            "failed" => "failed",
            "aborted" or "cancelled" or "canceled" => "cancelled",
            _ => null
        };
    }

    internal static IReadOnlyDictionary<string, OwnerConsoleGateProgress> MemberProjections(
        OwnerConsoleGateProgress[] snapshots, List<Ejection> ejections, DateTimeOffset now)
    {
        var views = new Dictionary<string, OwnerConsoleGateProgress>(StringComparer.Ordinal);
        foreach (var member in snapshots.SelectMany(gate => gate.Members).Select(member => member.Id)
            .Concat(ejections.Select(item => item.Goal)).Distinct(StringComparer.Ordinal))
        {
            var candidates = snapshots.Where(gate => gate.Members.Any(id => MemberMatches(id.Id, member))).ToArray();
            var dispositions = new List<OwnerConsoleGateProgress>();
            foreach (var group in candidates.GroupBy(gate => gate.OuterAttemptId ?? "execution:" + gate.ExecutionId + ":" + gate.StartedAt))
            {
                var current = group.LastOrDefault(gate => gate.Live) ?? group.Last();
                var ejection = ejections.LastOrDefault(item => MemberMatches(item.Goal, member) &&
                    (item.Outer is not null ? item.Outer == current.OuterAttemptId : current.ExecutionId is null && current.TrainIdentity == item.Train));
                if (ejection is not null && !(current.AdmittedAt > ejection.At && current.ExecutionId != ejection.Execution))
                {
                    var original = group.FirstOrDefault(gate => gate.ExecutionId == ejection.Execution) ?? current;
                    dispositions.Add(original with { Live = false, Participation = "ejected / awaiting survivor", Lanes = [],
                        LanePlanAvailable = false, EtaAvailable = false, DoneCount = 0, TotalCount = 0, RemainingSeconds = 0,
                        FinalOutcome = "ejected", FinishedAt = ejection.At, FinishedAge = Nonnegative(now - ejection.At) });
                }
                else if (current.Participation == "current execution unresolved")
                    dispositions.Add(Idle() with { Kind = "unresolved", OuterAttemptId = current.OuterAttemptId,
                        Members = [new(member, "")], Participation = current.Participation, LanePlanAvailable = false, EtaAvailable = false });
                else if (current.Live || current.Participation is not null) dispositions.Add(current);
                else if (current.FinishedAt is { } finished && now - finished <= TimeSpan.FromMinutes(15))
                    dispositions.Add(current with { Participation = current.FinalOutcome ?? "finished" });
            }
            // Independent live executions that share a member stay independent.
            // A goal selection cannot silently choose one by chronology.
            var live = dispositions.Where(gate => gate.Live).ToArray();
            var unlinkedEjection = ejections.LastOrDefault(item => MemberMatches(item.Goal, member) && item.Outer is null);
            if (unlinkedEjection is not null)
                live = live.Where(gate => gate.AdmittedAt > unlinkedEjection.At).ToArray();
            if (live.Length > 1)
                views[member] = Idle() with { Kind = "concurrent", Members = [new(member, "")],
                    Participation = $"{live.Length} live executions", ConcurrentRuns = live
                        .OrderBy(gate => gate.ExecutionId, StringComparer.Ordinal).ThenBy(gate => gate.OuterAttemptId, StringComparer.Ordinal).ToArray(),
                    LanePlanAvailable = false, EtaAvailable = false };
            else if (live.Length == 1) views[member] = live[0];
            else if (dispositions.Count > 0) views[member] = dispositions.LastOrDefault(gate =>
                gate.Participation == "ejected / awaiting survivor") ?? dispositions.Last();
        }
        return views;
    }

    internal static IEnumerable<Observation> AttributionObservations(Observation[] parsed, Gate gate) =>
        parsed.Where(item => gate.ExecutionId is not null && item.Get("scope") == "attribution" &&
                item.Get("parent_execution") == gate.ExecutionId &&
                item.Get("outer_attempt") == (gate.OuterAttemptId ?? "") &&
                ExecutionOf(item).Length > 0 &&
                gate.Members.Any(member => MemberMatches(member, item.Get("goal"))));

    internal static IEnumerable<OwnerConsoleGateLane> AttributionRows(Observation[] observations, Gate gate) =>
        observations.GroupBy(item => ExecutionOf(item), StringComparer.Ordinal)
            .Select(group =>
            {
                var latest = group.Last();
                var member = latest.Get("reproduction_member") is { Length: > 0 } full ? full : latest.Get("goal");
                var complete = latest.Get("phase") == "attribution-complete";
                return new OwnerConsoleGateLane($"attribution {Prefix(member)}: {group.Key}",
                    complete || !gate.Live ? OwnerConsoleGateLaneState.Done : OwnerConsoleGateLaneState.Running,
                    complete ? latest.Get("outcome") : "unknown", "",
                    TimeSpan.FromMilliseconds(latest.Number("elapsed_ms")), null, false,
                    IsManifestLane: false, Target: latest.Get("target"), IsAttribution: true,
                    EvidenceKind: "attribution", ReproductionMemberId: member, IsScheduled: false);
            });
}
