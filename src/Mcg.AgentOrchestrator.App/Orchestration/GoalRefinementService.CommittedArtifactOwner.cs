using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class GoalRefinementService
{
    private static Dictionary<string, string> RewriteCommittedArtifactOwners(
        List<string> criteria, List<string> diagnostics)
    {
        var rewrites = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < criteria.Count; index++)
        {
            var criterion = criteria[index];
            var rewritten = CommittedArtifactCriterionOwner.Rewrite(criterion, out var originalOwner);
            if (originalOwner is null)
                continue;

            criteria[index] = rewritten;
            rewrites.Add(criterion, rewritten);
            diagnostics.Add(
                $"Spec refiner owner rewritten: declared criterion {index + 1} requires a committed artifact; owner {originalOwner} -> Developer");
        }

        return rewrites;
    }
}
