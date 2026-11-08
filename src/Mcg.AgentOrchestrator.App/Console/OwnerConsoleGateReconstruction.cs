using static Mcg.AgentOrchestrator.App.OwnerConsole.OwnerConsoleGateProgress;
using static Mcg.AgentOrchestrator.App.OwnerConsole.OwnerConsoleGateExecutionProjection;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

// Mutable replay state belongs to one Reconstruct call and is never shared.
internal sealed class OwnerConsoleGateReconstruction
{
    internal Dictionary<string, Gate> Gates { get; } = new(StringComparer.OrdinalIgnoreCase);
    internal Dictionary<string, Observation> Owners { get; } = new(StringComparer.Ordinal);
    internal HashSet<string> CompletedOwners { get; } = new(StringComparer.Ordinal);
    internal List<Ejection> Ejections { get; } = [];
    internal DateTimeOffset? LastEnd { get; set; }

    internal Gate Admit(Observation item, string[] members, string execution, string outer, string train,
        string phase, string detail, bool solo)
    {
        var key = execution.Length > 0 ? "execution:" + execution : "unresolved:" +
            string.Join('+', members.Select(Prefix).Order(StringComparer.Ordinal));
        var starts = phase == "execution-admitted" ||
            detail.StartsWith("ACCEPTANCE_COHORT_ENTRY ", StringComparison.Ordinal) || solo && item.Get("result") == "started";
        if (!Gates.TryGetValue(key, out var gate) || starts && gate.ExecutionId is null)
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
            if (execution.Length > 0 && phase != "execution-admitted" && Ejections.Any(ejection =>
                (ejection.Outer is null || ejection.Outer == outer) && members.Any(id => MemberMatches(id, ejection.Goal))))
            {
                gate.Live = false;
                gate.Participation = "participation unresolved after ejection";
            }
            Gates[key] = gate;
            if (outer.Length > 0 && execution.Length > 0 && !CompletedOwners.Contains(outer))
                foreach (var previous in Gates.Values.Where(previous => previous != gate && previous.OuterAttemptId == outer))
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
        return gate;
    }

    internal bool ApplyCompletion(Gate gate, Observation item, string outer, bool terminal)
    {
        // A retained outer completion also governs children first seen later
        // in a truncated tail. Neither progress nor admission reopens that owner.
        if (CompletedOwners.Contains(outer))
        {
            gate.Live = false;
            gate.Participation = "outer attempt completed";
            if (gate.FinishedAt is null)
            {
                gate.FinalOutcome = FinalOutcomeOf(Owners[outer]);
                gate.FinishedAt = Owners[outer].Event.Timestamp;
            }
        }
        if (terminal)
        {
            gate.Live = false;
            gate.FinalOutcome = FinalOutcomeOf(item);
            gate.FinishedAt = item.Event.Timestamp;
            LastEnd = item.Event.Timestamp;
            return true;
        }
        return false;
    }

    internal void Track(Gate gate, Observation item, string execution, string outer, string phase, string[] members)
    {
        if (!gate.Live && (gate.Participation is "participation unresolved after ejection" or "current execution unresolved") && phase == "execution-admitted")
        {
            gate.Live = true;
            gate.Participation = null;
            foreach (var previous in Gates.Values.Where(previous => previous != gate &&
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
            return; // Late progress cannot resurrect a completed execution.
        }
        if (execution.Length > 0 &&
            (gate.OuterAttemptId != (outer.Length == 0 ? null : outer) ||
             gate.Members.Length != members.Length || !gate.Members.All(id => members.Any(other => MemberMatches(id, other))) ||
             AttemptFromHeartbeat(item.Get("heartbeat")) is { Length: > 0 } heartbeatAttempt &&
                !heartbeatAttempt.Equals(execution, StringComparison.OrdinalIgnoreCase)))
        {
            gate.Live = false;
            gate.Participation = "conflicting execution identity or membership";
            return;
        }
        if (phase == "execution-admitted" && execution.Length > 0) gate.AdmittedAt ??= item.Event.Timestamp;
        gate.Events.Add(item);
        gate.Last = item.Event.Timestamp;
    }
}
