using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class MultiRepositoryLandingRepairIntentHandler
{
    internal static void Apply(Goal goal, MultiRepositoryLandingRepair repair)
    {
        var result = repair.Run(goal);
        if (!result.Repaired) throw new InvalidOperationException($"{result.Reason}: {result.Detail}");
    }
}
