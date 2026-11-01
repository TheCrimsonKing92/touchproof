using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Cli;

// A read-only partition of obligations for one candidate transition; never evidence itself.
internal sealed class GoalBranchCommitRebindPlan
{
    internal List<CriterionEvidenceObligation> Eligible { get; } = [];
    internal Dictionary<string, string> Unchanged { get; } = new(StringComparer.Ordinal);

    internal static GoalBranchCommitRebindPlan Create(Goal goal, string previousHead, string newHead) =>
        Create(goal.CriterionEvidenceObligations, goal.AuthoritativeRefinedSpecVersion?.Version ?? 0,
            previousHead, newHead);

    internal static GoalBranchCommitRebindPlan Create(IEnumerable<CriterionEvidenceObligation> obligations,
        int currentVersion, string previousHead, string newHead)
    {
        var plan = new GoalBranchCommitRebindPlan();
        foreach (var duty in obligations.OrderBy(item => item.CriterionVersion).ThenBy(item => item.CriterionIndex))
        {
            var reason = duty.CriterionVersion != currentVersion ? "historical-version"
                : duty.State == CriterionEvidenceState.Repaired ? "repaired"
                : duty.Phase != CriterionEvidencePhase.PreLanding ? "post-landing"
                : duty.Owner == CriterionEvidenceOwner.Operator ? "operator-owned"
                : duty.Owner != CriterionEvidenceOwner.Acceptance ? "not-acceptance-owned"
                : string.IsNullOrWhiteSpace(duty.ExpectedCandidateSha) ? "unbound"
                : string.Equals(duty.ExpectedCandidateSha, newHead, StringComparison.OrdinalIgnoreCase) ? "already-bound-to-new-head"
                : !string.Equals(duty.ExpectedCandidateSha, previousHead, StringComparison.OrdinalIgnoreCase) ? "bound-to-other-sha"
                : null;
            if (reason is null) plan.Eligible.Add(duty);
            else plan.Unchanged.Add(duty.Id, reason);
        }
        return plan;
    }

    internal static string StableId(GoalId goalId, CriterionEvidenceObligation duty) =>
        $"rebind-{goalId.Value[..8]}-criterion-{duty.CriterionIndex}";

    internal static string RebindKey(GoalId goalId, string newHead, CriterionEvidenceObligation duty) =>
        $"goal-branch-commit-rebind:{goalId.Value}:{newHead}:{duty.CriterionNumber}:v{duty.CriterionVersion}";

    internal static CriterionEvidenceMappingOperatorIntentPayload Mapping(GoalId goalId, string newHead,
        CriterionEvidenceObligation duty) => new(duty.CriterionIndex, duty.CriterionVersion,
            CriterionEvidenceOwner.Acceptance, CriterionEvidenceScopes.FullAcceptanceGate, StableId(goalId, duty), newHead);
}
