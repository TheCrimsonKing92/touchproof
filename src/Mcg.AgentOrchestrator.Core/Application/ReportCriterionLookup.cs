namespace Mcg.AgentOrchestrator.Core;

// Resolves current-brief report criteria when unchanged criteria retain an older spec stamp.
internal static class ReportCriterionLookup
{
    internal static string? Find(Goal goal, PlannerEvidenceIdentity? identity) => Find(goal, identity, out _);

    internal static string? Find(Goal goal, PlannerEvidenceIdentity? identity, out int verdictIndex)
    {
        verdictIndex = -1;
        var version = goal.AuthoritativeRefinedSpecVersion;
        if (identity is null || identity.BriefVersion != goal.AuthoritativeBrief.Version ||
            version is null || version.BriefVersion > identity.BriefVersion)
            return null;

        var index = identity.CriterionIndex - 1;
        if (index < 0 || index >= version.Spec.AcceptanceCriteria.Count ||
            string.IsNullOrWhiteSpace(version.Spec.AcceptanceCriteria[index]))
            return null;
        verdictIndex = index;
        return version.Spec.AcceptanceCriteria[index];
    }
}
