using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Owns removal and audit of terminal dependency edges before a goal starts work.
internal static class ConductorDependencyEdgeReconciler
{
    internal static string? Evaluate(
        Goal goal,
        HashSet<string> completedGoals,
        HashSet<string> escalatedGoals,
        AgentOrchestratorKernel kernel,
        out bool requiresPerson,
        IReadOnlySet<string> dependencyEscalatedGoals,
        HashSet<GoalId> changedGoalIds)
    {
        while (true)
        {
            var reason = ConductorDependencyHoldEvaluator.Evaluate(
                goal, completedGoals, escalatedGoals, kernel, out requiresPerson,
                out var terminalDependencyId, out var terminalStatus, dependencyEscalatedGoals);
            if (terminalDependencyId is not { } dependencyId || terminalStatus is null ||
                !ConductorDependencyHoldEvaluator.IsRemovableTerminalDependencyStatus(terminalStatus))
            {
                return reason;
            }

            try
            {
                kernel.RemoveGoalDependency(goal.Id, dependencyId);
            }
            catch (Exception exception) when (exception is InvalidOperationException or KeyNotFoundException)
            {
                return reason;
            }

            kernel.RecordGoalPolicyDecision(goal.Id,
                $"DEPENDENCY_EDGE_DROPPED dependent={goal.Id.Value} dependency={dependencyId.Value} status={terminalStatus} reason=terminal-without-landing");
            changedGoalIds.Add(goal.Id);
            // Each successful pass removes one persisted edge; evaluation retains all other holds.
            goal = kernel.GetGoal(goal.Id);
        }
    }
}
