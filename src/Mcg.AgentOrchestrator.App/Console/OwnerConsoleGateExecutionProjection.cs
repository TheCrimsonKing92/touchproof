namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal sealed partial record OwnerConsoleGateProgress
{
    // Identity is a recorded relationship, never a comparison of member sets.
    private sealed record Ejection(string? Outer, string Goal, string? Execution, DateTimeOffset At, string? Train);

    private static string ExecutionOf(Observation item)
    {
        var execution = item.Get("execution");
        if (execution.Length == 0 && item.Event.Detail.StartsWith("ACCEPTANCE ", StringComparison.Ordinal))
            execution = item.Get("attempt");
        return execution.Length > 0 ? execution : AttemptFromHeartbeat(item.Get("heartbeat"));
    }

    private static Dictionary<string, Gate> Reconstruct(Observation[] parsed,
        out List<Ejection> ejections, out DateTimeOffset? lastEnd)
    {
        var gates = new Dictionary<string, Gate>(StringComparer.OrdinalIgnoreCase);
        var owners = new Dictionary<string, Observation>(StringComparer.Ordinal);
        var completedOwners = new HashSet<string>(StringComparer.Ordinal);
        ejections = [];
        lastEnd = null;
        foreach (var item in parsed)
        {
            var detail = item.Event.Detail;
            var cohort = detail.StartsWith("ACCEPTANCE_COHORT", StringComparison.Ordinal);
            var solo = detail.StartsWith("ACCEPTANCE ", StringComparison.Ordinal);
            if (!cohort && !solo && item.Event.EventKind != "gate-progress") continue;
            // Partition run IDs describe auxiliary reproduction, never admission
            // to a new gate. Only an explicit parent link can attach their activity.
            if (item.Get("scope") == "attribution") continue;
            var execution = ExecutionOf(item);
            var outer = item.Get("outer_attempt");
            if (cohort && outer.Length == 0) outer = item.Get("attempt");
            var train = item.Get("train_identity");
            var phase = item.Get("phase");
            if (phase == "gate-phase-breakdown") continue;
            var members = (item.Get("participant_ids") is { Length: > 0 } full ? full : item.Get("members"))
                .Split([',', '+'], StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.Ordinal).ToArray();
            var goal = item.Get("goal");
            if (goal.Length == 0) goal = item.Event.GoalId ?? "";
            var legacyEjection = System.Text.RegularExpressions.Regex.Match(detail, @"ejected from train (?<train>[^\s"";]+)");
            if (phase == "member-ejected" || legacyEjection.Success)
            {
                if (goal.Length == 0) continue;
                if (train.Length == 0 && legacyEjection.Success) train = legacyEjection.Groups["train"].Value;
                // Older goal-specific receipts identify an ejection even when the
                // parent relationship was not recorded. Retain it as unresolved.
                var original = gates.Values.LastOrDefault(gate => outer.Length > 0 && gate.OuterAttemptId == outer &&
                    (execution.Length > 0 ? gate.ExecutionId == execution : gate.TrainIdentity == train));
                ejections.Add(new(outer.Length == 0 ? null : outer, goal, original?.ExecutionId, item.Event.Timestamp, train));
                if (original is not null)
                {
                    original.Live = false;
                    original.Participation = "historical execution";
                }
                if (original is null)
                    gates["ejected:" + outer + ":" + goal + ":" + train] = new([goal], item.Event.Timestamp)
                    {
                        ExecutionId = execution.Length == 0 ? null : execution, OuterAttemptId = outer.Length == 0 ? null : outer,
                        TrainIdentity = train.Length == 0 ? null : train, Live = false, Participation = "ejected / awaiting survivor"
                    };
                continue;
            }
            var outcome = item.Get("outcome");
            var terminal = cohort && (detail.StartsWith("ACCEPTANCE_COHORT_CHILD_COMPLETED", StringComparison.Ordinal) ||
                detail.StartsWith("ACCEPTANCE_COHORT_RECONCILED_DEAD", StringComparison.Ordinal) ||
                detail.StartsWith("ACCEPTANCE_COHORT_ADOPTION_REFUSED", StringComparison.Ordinal) || detail.Contains("ABANDON", StringComparison.Ordinal) ||
                (detail.StartsWith("ACCEPTANCE_COHORT_EXIT ", StringComparison.Ordinal) || detail.StartsWith("ACCEPTANCE_COHORT ", StringComparison.Ordinal)) &&
                outcome is not ("" or "inflight" or "gathering" or "unpaired")) || solo && item.Get("result") is not ("" or "started" or "running");
            if (cohort && outer.Length > 0)
            {
                owners[outer] = item;
                if (terminal)
                {
                    completedOwners.Add(outer);
                    lastEnd = item.Event.Timestamp;
                    foreach (var child in gates.Values.Where(gate => gate.OuterAttemptId == outer)) child.Live = false;
                }
                continue; // Outer ownership never asserts execution participation.
            }
            if (cohort && members.Length == 0) continue;
            if (members.Length == 0 && goal.Length > 0 && goal != "unknown") members = [goal];
            if (members.Length == 0) continue;
            var key = execution.Length > 0 ? "execution:" + execution : "unresolved:" +
                string.Join('+', members.Select(Prefix).Order(StringComparer.Ordinal));
            var starts = phase == "execution-admitted" ||
                detail.StartsWith("ACCEPTANCE_COHORT_ENTRY ", StringComparison.Ordinal) || solo && item.Get("result") == "started";
            if (!gates.TryGetValue(key, out var gate) || starts && gate.ExecutionId is null)
            {
                gate = new(members, item.Event.Timestamp)
                {
                    ExecutionId = execution.Length == 0 ? null : execution,
                    OuterAttemptId = outer.Length == 0 ? null : outer,
                    TrainIdentity = train.Length == 0 ? null : train,
                    Participation = execution.Length == 0 ? "execution unresolved" : null
                };
                if (phase == "execution-admitted" && execution.Length == 0)
                {
                    gate.Live = false;
                    gate.Participation = "admission execution unresolved";
                }
                if (execution.Length > 0 && phase != "execution-admitted" && ejections.Any(ejection =>
                    (ejection.Outer is null || ejection.Outer == outer) && members.Any(id => MemberMatches(id, ejection.Goal))))
                {
                    gate.Live = false;
                    gate.Participation = "participation unresolved after ejection";
                }
                gates[key] = gate;
                if (outer.Length > 0 && execution.Length > 0)
                    foreach (var previous in gates.Values.Where(previous => previous != gate && previous.OuterAttemptId == outer))
                    {
                        previous.Live = false;
                        previous.Participation = phase == "execution-admitted" ? "historical execution" : "current execution unresolved";
                        if (phase != "execution-admitted" && gate.Participation is null)
                        {
                            gate.Live = false;
                            gate.Participation = "current execution unresolved";
                        }
                    }
            }
            // A retained outer completion also governs children first seen later
            // in a truncated tail. Neither progress nor admission reopens that owner.
            if (completedOwners.Contains(outer))
            {
                gate.Live = false;
                gate.Participation = "outer attempt completed";
            }
            if (terminal)
            {
                gate.Live = false;
                lastEnd = item.Event.Timestamp;
                continue;
            }
            if (!gate.Live && (gate.Participation is "participation unresolved after ejection" or "current execution unresolved") && phase == "execution-admitted")
            {
                gate.Live = true;
                gate.Participation = null;
                foreach (var previous in gates.Values.Where(previous => previous != gate &&
                    gate.OuterAttemptId is not null && previous.OuterAttemptId == gate.OuterAttemptId))
                {
                    previous.Live = false;
                    previous.Participation = "historical execution";
                }
            }
            if (!gate.Live)
            {
                // Retain observations for explicit historical execution selection,
                // without asserting which unresolved child is current.
                if (gate.Participation is "participation unresolved after ejection" or "current execution unresolved" or "outer attempt completed")
                {
                    gate.Events.Add(item);
                    gate.Last = item.Event.Timestamp;
                }
                continue; // Late progress cannot resurrect a completed execution.
            }
            if (execution.Length > 0 &&
                (gate.OuterAttemptId != (outer.Length == 0 ? null : outer) ||
                 gate.Members.Length != members.Length || !gate.Members.All(id => members.Any(other => MemberMatches(id, other))) ||
                 AttemptFromHeartbeat(item.Get("heartbeat")) is { Length: > 0 } heartbeatAttempt &&
                    !heartbeatAttempt.Equals(execution, StringComparison.OrdinalIgnoreCase)))
            {
                gate.Live = false;
                gate.Participation = "conflicting execution identity or membership";
                continue;
            }
            if (phase == "execution-admitted" && execution.Length > 0) gate.AdmittedAt ??= item.Event.Timestamp;
            gate.Events.Add(item);
            gate.Last = item.Event.Timestamp;
        }
        // A truncated tail may contain ownership without any child evidence. Keep
        // that uncertainty visible instead of manufacturing a live train.
        foreach (var (outer, owner) in owners)
        {
            if (gates.Values.Any(gate => gate.OuterAttemptId == outer)) continue;
            var members = owner.Get("members").Split([',', '+'], StringSplitOptions.RemoveEmptyEntries);
            if (members.Length == 0) continue;
            gates["owner:" + outer] = new(members, owner.Event.Timestamp)
            {
                OuterAttemptId = outer, Live = false,
                Participation = completedOwners.Contains(outer) ? "outer attempt completed" : "awaiting execution evidence"
            };
        }
        return gates;
    }

    private static IReadOnlyDictionary<string, OwnerConsoleGateProgress> MemberProjections(
        OwnerConsoleGateProgress[] snapshots, List<Ejection> ejections)
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
                        LanePlanAvailable = false, EtaAvailable = false, DoneCount = 0, TotalCount = 0, RemainingSeconds = 0 });
                }
                else if (current.Participation == "current execution unresolved")
                    dispositions.Add(Idle() with { Kind = "unresolved", OuterAttemptId = current.OuterAttemptId,
                        Members = [new(member, "")], Participation = current.Participation, LanePlanAvailable = false, EtaAvailable = false });
                else if (current.Live || current.Participation is not null) dispositions.Add(current);
            }
            // Independent live executions that share a member stay independent.
            // A goal selection cannot silently choose one by chronology.
            var live = dispositions.Where(gate => gate.Live).ToArray();
            var unlinkedEjection = ejections.LastOrDefault(item => MemberMatches(item.Goal, member) && item.Outer is null);
            if (unlinkedEjection is not null)
                live = live.Where(gate => gate.AdmittedAt > unlinkedEjection.At).ToArray();
            if (live.Length > 1)
                views[member] = Idle() with { Kind = "unresolved", Members = [new(member, "")],
                    Participation = "multiple independent executions; select an execution", LanePlanAvailable = false, EtaAvailable = false };
            else if (live.Length == 1) views[member] = live[0];
            else if (dispositions.Count > 0) views[member] = dispositions.LastOrDefault(gate =>
                gate.Participation == "ejected / awaiting survivor") ?? dispositions.Last();
        }
        return views;
    }

    private static IEnumerable<Observation> AttributionObservations(Observation[] parsed, Gate gate) =>
        parsed.Where(item => gate.ExecutionId is not null && item.Get("scope") == "attribution" &&
                item.Get("parent_execution") == gate.ExecutionId &&
                item.Get("outer_attempt") == (gate.OuterAttemptId ?? "") &&
                ExecutionOf(item).Length > 0 &&
                gate.Members.Any(member => MemberMatches(member, item.Get("goal"))));

    private static IEnumerable<OwnerConsoleGateLane> AttributionRows(Observation[] observations, Gate gate) =>
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
