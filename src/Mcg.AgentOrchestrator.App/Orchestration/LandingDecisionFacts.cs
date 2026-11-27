using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class LandingDecisionFacts
{
    internal static bool IsConflict(string reason) =>
        reason.StartsWith("pre-landing rebase conflict (", StringComparison.Ordinal) ||
        reason.StartsWith("pre-merge rebase conflict (", StringComparison.Ordinal);

    internal static PolicyDecisionRecord WithIntegrationRefusal(PolicyDecisionRecord decision, string detail) => decision with
    {
        Reason = decision.Reason.Replace(" and does not run workspace rebase", "", StringComparison.Ordinal) +
            "; conductor integration refused: " + detail,
        Facts = Array.AsReadOnly<PolicyDecisionFact>([.. decision.Facts, new("conductorIntegrationRefusal", detail)])
    };

    internal static PolicyDecisionRecord ReplayDecision(ConductorAdvanceResult earlyResult, string detail)
    {
        if (earlyResult.Outcome is ConductorAdvanceOutcome.Escalated { Decision: { Stage: LandingRebasePolicy.StageName } recorded })
            return recorded with { Reason = detail };

        // Rehydrated attempt results retain the detail without the original rebase facts.
        var phase = detail.StartsWith("pre-merge rebase ", StringComparison.Ordinal) ? "pre-merge" : "pre-landing";
        var status = IsConflict(detail) ? "Conflict" : string.Empty;
        var facts = new LandingRebaseFacts(phase, false, status, [], detail, false);
        return LandingRebasePolicy.Evaluate(facts).ToRecord() with { Reason = detail };
    }

    internal static RacingLandingCarryForwardFacts ForRevalidationHold(AcceptanceMainAdvanceDisposition disposition) =>
        disposition switch
        {
            AcceptanceMainAdvanceDisposition.Overlap overlap =>
                new(nameof(AcceptanceMainAdvanceDisposition.Overlap), overlap.Kind, overlap.LandedPath,
                    overlap.VerifiedPath, string.Empty, overlap.FormatReceipt()),
            AcceptanceMainAdvanceDisposition.Unknown unknown =>
                new(nameof(AcceptanceMainAdvanceDisposition.Unknown), string.Empty, string.Empty,
                    string.Empty, unknown.Reason, unknown.FormatReceipt()),
            _ => throw new InvalidOperationException(
                $"Unsupported acceptance main-advance disposition {disposition.GetType().Name}.")
        };
}
