namespace Mcg.AgentOrchestrator.Core;

internal static class RefinedSpecBriefSection
{
    internal static void AppendTo(
        List<TaskBriefSegment> segments,
        Goal goal,
        AgentRole role,
        DeclaredTestRemovalBriefContext declarationContext)
    {
        if (goal.RefinedSpec is not { } refinedSpec)
            return;

        var criteria = refinedSpec.AcceptanceCriteria ?? [];
        var earlierLocations = role == AgentRole.Reviewer ? FindEarlierLocations(segments, criteria) : null;
        var specLines = new List<string>
        {
            earlierLocations is null
                ? "## Refined Spec"
                : $"Refined acceptance criteria: see {string.Join(", ", earlierLocations)} above.",
            $"Behavioral contract: {declarationContext.RenderBehavioralContract(refinedSpec.BehavioralContract)}"
        };
        if (earlierLocations is null)
        {
            specLines.Add(string.Empty);
            specLines.Add(declarationContext.HasAuthoritativePlannerCriteria
                ? "Acceptance criteria (authoritative list validated by PlannerOutputContract):"
                : "Acceptance criteria:");
        }

        for (var criterionIndex = 0; criterionIndex < criteria.Count; criterionIndex++)
        {
            var criterion = criteria[criterionIndex];
            var normalizedCriterion = criterion?.Trim();
            var waiver = goal.EffectiveAcceptanceCriteriaCorrections
                .Where(correction =>
                    correction.IsWaiver &&
                    string.Equals(correction.SupersededCriterion, normalizedCriterion, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(correction => correction.RecordedAt)
                .FirstOrDefault();
            if (waiver is null)
            {
                if (earlierLocations is null)
                    specLines.Add(declarationContext.HasAuthoritativePlannerCriteria
                        ? $"{criterionIndex + 1}. {criterion}"
                        : $"- {criterion}");
                continue;
            }

            specLines.Add(declarationContext.HasAuthoritativePlannerCriteria
                ? $"{criterionIndex + 1}. [WAIVED] {normalizedCriterion}"
                : $"- [WAIVED] {normalizedCriterion}");
            specLines.Add($"  Reason: {PromptContextFormatter.TrimPromptBlock(waiver.WaiverReason!)}");
            specLines.Add($"  Waived by {PromptContextFormatter.TrimPromptBlock(waiver.Actor)} at {waiver.RecordedAt:u}.");
        }
        if (refinedSpec.OperatorOwnedAcceptanceCriteria.Count > 0)
        {
            specLines.Add(string.Empty);
            specLines.Add("OPERATOR-OWNED / post-landing criteria (not part of worker acceptance):");
            foreach (var criterion in refinedSpec.OperatorOwnedAcceptanceCriteria)
                specLines.Add($"- {criterion}");
        }
        var acceptanceGateOwnedCriteria = refinedSpec.AcceptanceGateOwnedAcceptanceCriteria
            .Concat(goal.CriterionEvidenceObligations.Where(item =>
                item.CriterionVersion == goal.AuthoritativeRefinedSpecVersion?.Version &&
                item.Owner == CriterionEvidenceOwner.Acceptance && item.PlannerSources is { Count: > 0 } &&
                goal.HasCurrentPlannerExecutionAuthority(item))
                .Select(item => item.Criterion))
            .Distinct(StringComparer.Ordinal)
            .Where(criterion => ReviewerExecutedCriterionPresentation.ShowAsGateOwned(
                role, criterion, refinedSpec.OperatorOwnedAcceptanceCriteria))
            .ToArray();
        if (acceptanceGateOwnedCriteria.Length > 0)
        {
            specLines.Add(string.Empty);
            specLines.Add("ACCEPTANCE-GATE-OWNED criteria (the acceptance gate, not the worker, produces this evidence):");
            foreach (var criterion in acceptanceGateOwnedCriteria)
                specLines.Add($"- {criterion}");
            if (goal.CriterionEvidenceObligations.Any(item => item.PlannerSources is { Count: > 0 }))
                specLines.Add("Only execution evidence is deferred. Reviewer must independently pass source, safety and preserved-behavior review, including compound criteria; any source defect remains blocking.");
        }
        CurrentSpecificationBriefDetails.AppendTo(specLines, refinedSpec, goal.AuthoritativeBrief.Version);
        segments.Add(TaskBriefSegment.Projected("goal/refined-spec.json", specLines));
    }

    private static string[]? FindEarlierLocations(
        IReadOnlyList<TaskBriefSegment> segments,
        IReadOnlyList<string> criteria)
    {
        if (criteria.Count == 0)
            return null;

        var segmentTexts = segments.Select(segment => NormalizeWhitespace(string.Join(' ', segment.Lines))).ToArray();
        var matchedSegments = new HashSet<int>();
        foreach (var criterion in criteria)
        {
            var text = NormalizeWhitespace(criterion);
            if (text.Length == 0)
                return null;

            var index = Array.FindIndex(segmentTexts, segment => segment.Contains(text, StringComparison.Ordinal));
            if (index < 0)
                return null;

            matchedSegments.Add(index);
        }

        return matchedSegments.Order()
            .Select(index => DescribeLocation(segments[index]))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static string NormalizeWhitespace(string? text) =>
        string.Join(' ', (text ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static string DescribeLocation(TaskBriefSegment segment)
    {
        var heading = segment.Lines.FirstOrDefault(line => line.StartsWith('#'));
        return heading is null
            ? "the earlier brief text"
            : NormalizeWhitespace(heading.TrimStart('#'));
    }
}
