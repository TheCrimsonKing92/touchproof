using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class CliMultiRepositoryGateHold
{
    internal static bool Apply(CliExecutionContext context, Goal goal)
    {
        if (!MultiRepositoryGateHold.Applies(goal)) return false;
        var hold = MultiRepositoryGateHold.Create(goal, GoalLifecycleState.Verified);
        ConductorLandingBlockerRecorder.Observe(context.Kernel, goal, hold, DateTimeOffset.UtcNow, []);
        new MultiRepositoryGateHoldAttention(CollaborationItemStore.ForDirectory(context.Workspace.OrchestratorDirectory)).Reconcile(goal);
        Console.WriteLine(hold.Reason);
        return true;
    }
}
