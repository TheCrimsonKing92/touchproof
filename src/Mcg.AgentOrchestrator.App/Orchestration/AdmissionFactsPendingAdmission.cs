using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class AdmissionFactsPendingAdmission
{
    internal static bool IsPending(ConductorAdvanceResult result) => result.Outcome is
        ConductorAdvanceOutcome.Held { Decision.DiscriminatingEvidence: "admission-facts-pending" };

    internal static Goal[] Apply(IEnumerable<Goal> ordered, IEnumerable<ConductorSpeculativeAcceptanceCandidate> candidates,
        IDictionary<string, ParallelLandingOutcome> results, ConductorAutonomyPolicy policy, int tick, Action<string> progress,
        ConductorDriver? driver = null)
    {
        var all = candidates.ToDictionary(candidate => candidate.GoalId);
        // Reconciliation can already have landed a goal and advanced main this tick.
        // A miss for the new main must not replace that result (or an existing hold/escalation).
        var pending = all.Values.Where(candidate => !results.ContainsKey(candidate.GoalId.Value) &&
            candidate.ProjectionResult is GateReadyCandidateProjectionResult.Excluded
            { Reason: GateReadyCandidateExclusionReason.AdmissionFactsPending }).ToDictionary(candidate => candidate.GoalId);
        foreach (var id in pending.Keys.ToArray())
            foreach (var partner in driver?.AdmissionFacts.PendingReceiptPartners(id) ?? [])
                if (all.ContainsKey(partner) && !pending.ContainsKey(partner) && !results.ContainsKey(partner.Value))
                    pending[partner] = new(partner, new GateReadyCandidateProjectionResult.Excluded(GateReadyCandidateExclusionReason.AdmissionFactsPending)
                    { HoldReason = $"receipt-member={id.Value};{driver!.AdmissionFacts.Reader!.Inputs(partner)?.Identity}" });
        foreach (var (id, candidate) in pending)
        {
            var inputs = ((GateReadyCandidateProjectionResult.Excluded)candidate.ProjectionResult).HoldReason ?? "unresolved";
            var reason = $"admission-facts-pending goal={id.Value} inputs={inputs}";
            results[id.Value] = new(new(id.Value, id.Value[..8], policy.Name,
                new ConductorAdvanceOutcome.Held(GoalLifecycleState.Verified, reason, "admission-facts:" + id.Value)
                {
                    Owner = ConductorHoldOwner.AcceptanceQueue,
                    Decision = new PolicyDecisionRecord("admission-facts", "Hold", 1, "admission-facts-pending", reason,
                        Array.AsReadOnly<PolicyDecisionFact>([new("goalId", id.Value), new("inputs", inputs)]))
                }), null);
            progress($"ADMISSION tick={tick} result=held reason=admission-facts-pending goal={id.Value[..8]} inputs={inputs}");
        }
        return ordered.Where(goal => !pending.ContainsKey(goal.Id)).ToArray();
    }
}
