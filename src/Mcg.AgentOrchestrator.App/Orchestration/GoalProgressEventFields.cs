using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Additive event facts; the compact detail line remains a separate compatibility surface.
internal sealed record GoalProgressEventFields(string? GoalId = null, string? Result = null,
    string? State = null, int? Slot = null, string? Reason = null, PolicyDecisionRecord? Decision = null,
    ProgressFailurePayload? Failure = null)
{
    internal static GoalProgressEventFields? From(string goalId, ConductorAdvanceOutcome outcome, int? slot) => outcome switch
    {
        ConductorAdvanceOutcome.Executed e => new(goalId, "executed", e.FromState.ToString(), slot, Decision: e.Decision),
        ConductorAdvanceOutcome.Held h => new(goalId, "held", h.State.ToString(), slot, h.Reason, h.Decision),
        ConductorAdvanceOutcome.Escalated e => new(goalId, "escalated", e.State.ToString(), slot, e.Reason, e.Decision, e.Failure),
        ConductorAdvanceOutcome.Done d => new(goalId, "done", d.State.ToString(), slot, Decision: d.Decision),
        _ => null
    };

    // Narration de-duplicates re-read log entries by value, including ordered collections.
    public bool Equals(GoalProgressEventFields? other) => other is not null &&
        GoalId == other.GoalId && Result == other.Result && State == other.State && Slot == other.Slot &&
        Reason == other.Reason && Equals(Failure, other.Failure) &&
        (Decision is null ? other.Decision is null : other.Decision is { } decision &&
            Decision.Stage == decision.Stage && Decision.Action == decision.Action && Decision.Rung == decision.Rung &&
            Decision.DiscriminatingEvidence == decision.DiscriminatingEvidence && Decision.Reason == decision.Reason &&
            Decision.Facts.SequenceEqual(decision.Facts));

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(GoalId); hash.Add(Result); hash.Add(State); hash.Add(Slot); hash.Add(Reason); hash.Add(Failure);
        if (Decision is { } decision)
        {
            hash.Add(decision.Stage); hash.Add(decision.Action); hash.Add(decision.Rung);
            hash.Add(decision.DiscriminatingEvidence); hash.Add(decision.Reason);
            foreach (var fact in decision.Facts) hash.Add(fact);
        }
        return hash.ToHashCode();
    }
}
