using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static partial class AcceptanceCriterionEvidence
{
    internal sealed record RefusedCarryBinding(string ObligationId, string BoundSha);

    internal static CriterionEvidenceObligation[] StaleFullGateBindings(Goal goal, string? candidateSha) =>
        goal.GetLandingPrerequisiteObligations(candidateSha)
            .Where(item => item.Owner == CriterionEvidenceOwner.Acceptance &&
                !string.IsNullOrWhiteSpace(item.ExpectedCandidateSha) &&
                !string.Equals(item.ExpectedCandidateSha, candidateSha, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(item.RequiredScope, CriterionEvidenceScopes.FullAcceptanceGate, StringComparison.Ordinal))
            .ToArray();

    internal static RefusedCarryBinding BindingOf(CriterionEvidenceObligation obligation) =>
        new(obligation.Id, obligation.ExpectedCandidateSha!.ToLowerInvariant());

    internal static bool IsFullGateCarryRefused(
        Goal goal, string candidateSha, string integrationBranch, string? executionDirectory, out string? refusal)
    {
        return !TryPlanPatchEquivalentBindings(goal, candidateSha, integrationBranch, executionDirectory,
            out var eligible, out _, out _, out refusal, currentHeadSha: candidateSha) ||
            StaleFullGateBindings(goal, candidateSha).Any(item => !eligible.Contains(item));
    }

    internal const string RefusedCarryAdmissionReason = "criterion-binding-refused-carry";

    internal static ConductorAdvanceOutcome.Held DescribeRefusedCarryHold(
        Goal goal, string candidateSha, string mainSha,
        string passedBranchSha, string? passedMainSha,
        IReadOnlyList<CriterionEvidenceObligation> stale, string? refusal)
    {
        var bindings = string.Join(", ", stale.Select(item =>
            $"`{item.Id}` (v{item.CriterionVersion}) to {item.ExpectedCandidateSha}"));
        var diagnostic = $"Full acceptance deferred for goal {goal.Id.Value}, candidate {candidateSha} on main {mainSha}. " +
            $"Outstanding bindings: {bindings}. Carry-forward refused: {refusal ?? "reason=not-eligible"}.";
        var reason = FormatOperatorRebind(goal, candidateSha, stale, diagnostic) +
            $" Prior pass covered {passedBranchSha} on main {passedMainSha ?? "unknown"}.";
        return new ConductorAdvanceOutcome.Held(GoalLifecycleState.Verified, reason,
            StableIdentity: HoldIdentity(goal, candidateSha))
        {
            Decision = new PolicyDecisionRecord("criterion-evidence-admission", "Hold", 2,
                RefusedCarryAdmissionReason, reason,
                [
                    new PolicyDecisionFact("goalId", goal.Id.Value),
                    new PolicyDecisionFact("candidateSha", candidateSha),
                    new PolicyDecisionFact("mainSha", mainSha),
                    new PolicyDecisionFact("historicalCandidateSha", passedBranchSha),
                    new PolicyDecisionFact("historicalMainSha", passedMainSha ?? string.Empty),
                    new PolicyDecisionFact("obligationIds", string.Join(",", stale.Select(item => item.Id))),
                    new PolicyDecisionFact("nextActor", "Operator"),
                    new PolicyDecisionFact("nextAction", "criterion-evidence-map")
                ])
        };
    }

    private static string FormatOperatorRebind(Goal goal, string candidateSha,
        IReadOnlyList<CriterionEvidenceObligation> stale, string diagnostic)
    {
        var commands = stale.Select(item =>
            $"criterion-evidence-map --goal {goal.Id.Value} --criterion {item.CriterionNumber} --version {item.CriterionVersion} acceptance {item.RequiredScope} {item.FindingStableId ?? item.Id} {candidateSha}");
        return $"{diagnostic} Operator rebind: {string.Join("; ", commands)}";
    }

    private static string FormatRefusedCarryDiagnostic(
        string candidateSha, CriterionEvidenceObligation obligation, string? refusal) =>
        $"Acceptance passed for {candidateSha}, but obligation '{obligation.Id}' is bound to {obligation.ExpectedCandidateSha ?? "no candidate"}. Rebind the obligation to the current candidate before recording its evidence. Carry-forward refused: {refusal ?? "reason=not-eligible"}.";

    private static string FormatStaleBindingsDiagnostic(
        string candidateSha, CriterionEvidenceObligation obligation,
        IEnumerable<CriterionEvidenceObligation> stale, string? refusal)
    {
        var diagnostic = FormatRefusedCarryDiagnostic(candidateSha, obligation, refusal);
        var others = stale
            .Where(item => !string.Equals(item.Id, obligation.Id, StringComparison.Ordinal))
            .Select(item => $"'{item.Id}' to {item.ExpectedCandidateSha}")
            .ToArray();
        return others.Length == 0
            ? diagnostic
            : $"{diagnostic} Also bound to other candidates: {string.Join("; ", others)}.";
    }

    private static bool TryPlanPatchEquivalentBindings(
        Goal goal, string candidateSha, string integrationBranch, string? executionDirectory,
        out CriterionEvidenceObligation[] eligible,
        out string[] oldHeads,
        out Dictionary<string, string> evidenceByHead,
        out string? refusal, string? currentHeadSha = null)
    {
        refusal = null;
        eligible = goal.GetLandingPrerequisiteObligations(candidateSha)
            .Where(item => item.Phase == CriterionEvidencePhase.PreLanding &&
                !string.IsNullOrWhiteSpace(item.ExpectedCandidateSha) &&
                !string.Equals(item.ExpectedCandidateSha, candidateSha, StringComparison.OrdinalIgnoreCase) &&
                (item.Owner == CriterionEvidenceOwner.Acceptance &&
                 (item.State == CriterionEvidenceState.Pending ||
                  item.State == CriterionEvidenceState.Satisfied && !string.IsNullOrWhiteSpace(executionDirectory)) &&
                 string.Equals(item.RequiredScope, CriterionEvidenceScopes.FullAcceptanceGate, StringComparison.Ordinal) ||
                 item.Owner == CriterionEvidenceOwner.Operator &&
                 item.State == CriterionEvidenceState.Satisfied))
            .ToArray();
        var eligibleBindings = eligible;
        oldHeads = eligible
            .OrderBy(item => item.Owner == CriterionEvidenceOwner.Acceptance && item.State == CriterionEvidenceState.Satisfied)
            .Select(item => item.ExpectedCandidateSha!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        evidenceByHead = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (eligible.Length == 0) return false;
        if (eligible.Any(item => item.Owner == CriterionEvidenceOwner.Acceptance && item.State == CriterionEvidenceState.Satisfied) &&
            goal.GetLandingPrerequisiteObligations(candidateSha).Any(item =>
                item.Owner == CriterionEvidenceOwner.Acceptance &&
                string.Equals(item.RequiredScope, CriterionEvidenceScopes.FullAcceptanceGate, StringComparison.Ordinal) &&
                !string.Equals(item.ExpectedCandidateSha, candidateSha, StringComparison.OrdinalIgnoreCase) &&
                !eligibleBindings.Contains(item)))
            return false;
        if (string.IsNullOrWhiteSpace(executionDirectory))
        {
            refusal = "reason=no-execution-directory";
            return false;
        }

        foreach (var oldHead in oldHeads)
        {
            if (!GoalWorktrees.TryComputePatchEquivalence(
                    executionDirectory, oldHead, candidateSha, integrationBranch,
                    boundCandidateCarry: true, out var evidence, out var reason, out var evaluatedHead))
            {
                if (IntegrationOnlyCandidateDrift.TryProve(executionDirectory, oldHead, candidateSha,
                        integrationBranch, GoalWorktrees.BranchName(goal.Id), out var drift))
                {
                    evidenceByHead.Add(oldHead, drift);
                    continue;
                }
                if (currentHeadSha is not null &&
                    string.Equals(currentHeadSha, candidateSha, StringComparison.OrdinalIgnoreCase) &&
                    eligibleBindings.Where(item => string.Equals(item.ExpectedCandidateSha, oldHead, StringComparison.OrdinalIgnoreCase))
                        .All(item => item.Owner == CriterionEvidenceOwner.Acceptance && item.State == CriterionEvidenceState.Pending &&
                            string.Equals(item.RequiredScope, CriterionEvidenceScopes.FullAcceptanceGate, StringComparison.Ordinal)) &&
                    PassedHeadCandidateDrift.TryProve(executionDirectory, oldHead, candidateSha, integrationBranch, out var passedHead))
                {
                    evidenceByHead.Add(oldHead, passedHead);
                    continue;
                }
                refusal = eligible.Any(item => item.Owner == CriterionEvidenceOwner.Acceptance &&
                    item.State == CriterionEvidenceState.Satisfied &&
                    string.Equals(item.ExpectedCandidateSha, oldHead, StringComparison.OrdinalIgnoreCase))
                    && !eligible.Any(item =>
                        (item.Owner == CriterionEvidenceOwner.Operator || item.State == CriterionEvidenceState.Pending) &&
                        string.Equals(item.ExpectedCandidateSha, oldHead, StringComparison.OrdinalIgnoreCase))
                        ? null
                        : CarryForwardRefusalText.Format(reason, oldHead, evaluatedHead);
                return false;
            }
            evidenceByHead.Add(oldHead, evidence);
        }
        return true;
    }
}
