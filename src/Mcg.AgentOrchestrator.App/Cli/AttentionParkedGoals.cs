using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class AttentionParkedGoals
{
    internal static HashSet<string> IdsForShow(AgentOrchestratorKernel kernel)
    {
        // Legacy park markers also suppress records in the open view.
        var legacyParkWaitGoalIds = kernel.HumanInputRequests
            .Where(request => !request.IsCompleted && GoalParkResolution.IsParkResolution(request.Question))
            .Select(request => request.GoalId)
            .ToHashSet();
        return kernel.Goals
            .Where(goal => goal.Status == GoalStatus.Parked ||
                (goal.Status == GoalStatus.WaitingForHuman && legacyParkWaitGoalIds.Contains(goal.Id)))
            .Select(goal => goal.Id.Value)
            .ToHashSet(StringComparer.Ordinal);
    }

    internal static int ResolveOpenItems(AgentOrchestratorKernel kernel, CollaborationItemStore store)
    {
        var resolved = 0;
        foreach (var goal in kernel.Goals.Where(goal => goal.Status == GoalStatus.Parked))
        {
            var resolution = goal.Timeline
                .LastOrDefault(evt =>
                    evt.Kind == ProgressKind.GoalPolicyDecision &&
                    GoalParkResolution.IsParkResolution(evt.Message))
                ?.Message ?? GoalParkResolution.Fallback;
            resolved += store.ResolveOpenForGoalAsync(goal.Id.Value, resolution).GetAwaiter().GetResult();
        }

        return resolved;
    }
}
