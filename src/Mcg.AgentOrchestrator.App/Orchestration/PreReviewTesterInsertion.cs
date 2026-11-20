using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class PreReviewTesterInsertion
{
    internal static TaskSpec Add(
        AgentOrchestratorKernel kernel,
        IReadOnlyList<AgentDefinition> agents,
        GoalId goalId,
        string reason)
    {
        if (!agents.Any(agent => agent.Role == AgentRole.Tester && agent.Status == AgentStatus.Available))
        {
            throw new InvalidOperationException("No available Tester agent can be assigned.");
        }

        return kernel.AddTask(goalId, AgentRole.Tester, reason, agents, beforeRole: AgentRole.Reviewer);
    }

    internal static TaskSpec Unconfigured(GoalId goalId, string reason) =>
        throw new InvalidOperationException("Pre-review Tester add-task delegate was not configured.");

    internal static bool TryInsert(
        Func<GoalId, string, TaskSpec> addTask,
        GoalId goalId,
        string reason,
        out string? insertionFailure)
    {
        try
        {
            addTask(goalId, reason);
            insertionFailure = null;
            return true;
        }
        catch (Exception ex)
        {
            insertionFailure = $" Tester insertion failed: {ex.GetType().Name}: {ex.Message}";
            return false;
        }
    }
}
