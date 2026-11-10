namespace Mcg.AgentOrchestrator.Core;

/// <summary>Formats complete specification details for consumers without file access.</summary>
internal static class CurrentSpecificationBriefDetails
{
    internal static void AppendTo(List<string> specLines, RefinedSpec refinedSpec, int currentBriefVersion)
    {
        var detailsStart = specLines.Count;
        if (refinedSpec.Decisions.Count > 0)
        {
            specLines.Add(string.Empty);
            specLines.Add("Decisions:");
            foreach (var decision in refinedSpec.Decisions)
                specLines.Add($"- {decision.Question} → {decision.Choice} ({decision.Rationale})");
        }
        var authoritativeClarifications = refinedSpec.AuthoritativeClarificationAnswerHistory;
        if (authoritativeClarifications.Count > 0)
        {
            specLines.Add(string.Empty);
            specLines.Add("Clarification answer provenance:");
            foreach (var answer in authoritativeClarifications)
            {
                var answeredUnder = answer.BriefVersion is { } briefVersion
                    ? $"brief v{briefVersion}"
                    : "an unknown brief version";
                specLines.Add(
                    $"- {answer.Id} (answered under {answeredUnder}; " +
                    $"current brief v{currentBriefVersion}): {answer.Text}");
            }
        }
        specLines.Add(string.Empty);
        var sourceBytes = CurrentSpecificationProjection.SourceBytes(refinedSpec);
        var projectedBytes = CurrentSpecificationProjection.Project(refinedSpec);
        if (!sourceBytes.AsSpan().SequenceEqual(projectedBytes))
        {
            // Inline-only consumers get the same complete view as file-access dispatch.
            // The derived body references resolve locally, without opening any file.
            var details = System.Text.Json.Nodes.JsonNode.Parse(projectedBytes)!.AsObject();
            foreach (var field in new[] { "BehavioralContract", "AcceptanceCriteria", "VerificationClass", "OperatorOwnedAcceptanceCriteria" })
                details.Remove(field);
            specLines.RemoveRange(detailsStart, specLines.Count - detailsStart);
            specLines.Add("Decisions, questions and clarification answer provenance (local body references):");
            specLines.Add(details.ToJsonString());
            specLines.Add(string.Empty);
        }
        else if (refinedSpec.OpenQuestions.Count > 0)
        {
            specLines.Add("Questions (complete records):");
            specLines.Add(System.Text.Json.JsonSerializer.Serialize(refinedSpec.OpenQuestions));
        }
    }
}
