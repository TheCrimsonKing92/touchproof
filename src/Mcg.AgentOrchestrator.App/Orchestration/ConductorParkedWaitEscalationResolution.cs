using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Resolving a parked-wait escalation is best effort and cannot prevent lifecycle advancement.
internal static class ConductorParkedWaitEscalationResolution
{
    internal static void Resolve(Action<Goal, GoalLifecycleState> resolve, Goal goal, GoalLifecycleState state)
    {
        try
        {
            resolve(goal, state);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"warning: parked-wait landing escalation resolution failed for goal {goal.Id.Value[..8]}: {ex.Message}");
        }
    }
}
