namespace Mcg.AgentOrchestrator.Core;

public static class PreReviewTesterTaskInsertion
{
    public static TaskSpec Add(
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
}
