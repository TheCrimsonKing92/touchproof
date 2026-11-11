using Mcg.AgentOrchestrator.Core;
using System.Text;
using System.Text.Json;
using WorkerContextSemanticSource = Mcg.AgentOrchestrator.Infrastructure.WorkerProfileDispatcher.WorkerContextSemanticSource;

namespace Mcg.AgentOrchestrator.Infrastructure;

/// <summary>Contributes current delivery views and their preserved authoritative sources.</summary>
internal static class WorkerSpecificationArtifacts
{
    internal static bool AddObjective(
        Goal goal,
        Action<WorkerContextSemanticSource, string, ContextArtifactKind, byte[], ContextDeliveryMode?> addSource)
    {
        var projectedObjective = CurrentSpecificationProjection.ProjectObjective(goal.Objective, goal.RefinedSpec);
        var objectiveChanged = !projectedObjective.Equals(goal.Objective, StringComparison.Ordinal);
        addSource(WorkerContextSemanticSource.GoalObjective, "goal/objective.md", ContextArtifactKind.TaskObjective,
            Encoding.UTF8.GetBytes(goal.Objective), objectiveChanged ? ContextDeliveryMode.OnDemandFile : null);
        if (objectiveChanged)
            addSource(WorkerContextSemanticSource.GoalObjective, "goal/current-objective.md", ContextArtifactKind.TaskObjective,
                Encoding.UTF8.GetBytes(projectedObjective), null);
        return objectiveChanged;
    }

    internal static void AddSpecification(
        Goal goal,
        bool objectiveChanged,
        Action<WorkerContextSemanticSource, string, ContextArtifactKind, byte[], ContextDeliveryMode?> addSource)
    {
        var projectionChanged = false;
        if (goal.RefinedSpec is { } refinedSpec)
        {
            var sourceBytes = CurrentSpecificationProjection.SourceBytes(refinedSpec);
            var projectedBytes = CurrentSpecificationProjection.Project(refinedSpec);
            projectionChanged = !sourceBytes.AsSpan().SequenceEqual(projectedBytes);
            addSource(
                WorkerContextSemanticSource.RefinedSpec,
                "goal/refined-spec.json",
                ContextArtifactKind.AcceptanceCriteria,
                sourceBytes,
                projectionChanged ? ContextDeliveryMode.OnDemandFile : null);
            if (projectionChanged)
                addSource(WorkerContextSemanticSource.RefinedSpec, "goal/current-specification.json", ContextArtifactKind.AcceptanceCriteria,
                    projectedBytes, null);
        }
        // Actual stored versions and supersession relationships, never reconstructed history.
        if (objectiveChanged || goal.BriefVersions.Count > 1 || goal.RefinedSpecVersions.Count > 1 || projectionChanged)
            addSource(WorkerContextSemanticSource.RefinedSpec, "goal/specification-history.json", ContextArtifactKind.RegisteredContext,
                JsonSerializer.SerializeToUtf8Bytes(new { goal.BriefVersions, goal.RefinedSpecVersions }),
                ContextDeliveryMode.OnDemandFile);
    }
}
