namespace Mcg.AgentOrchestrator.Core;

/// <summary>Composes persisted intake evidence and declarations in worker-brief order.</summary>
internal static class GoalIntakeBriefSections
{
    internal static IEnumerable<TaskBriefSegment> Build(
        Goal goal,
        AgentRole role,
        DeclaredTestRemovalBriefContext declarationContext,
        IEnumerable<HumanInputRequest> requests)
    {
        foreach (var segment in RemovalReferenceCensusBriefSection.Segments(goal, role))
            yield return segment;
        if (declarationContext.RemovalLines.Count > 0)
            yield return TaskBriefSegment.Fixed(declarationContext.RemovalLines);
        var plannerEvidenceLines = PlannerEvidenceBriefSection.Build(goal, requests);
        if (plannerEvidenceLines.Count > 0)
            yield return TaskBriefSegment.Fixed(plannerEvidenceLines);
    }
}
