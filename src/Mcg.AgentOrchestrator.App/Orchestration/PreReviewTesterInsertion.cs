using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class PreReviewTesterInsertion
{
    internal static TaskSpec Add(
        AgentOrchestratorKernel kernel,
        IReadOnlyList<AgentDefinition> agents,
        GoalId goalId,
        string reason) =>
        PreReviewTesterTaskInsertion.Add(kernel, agents, goalId, reason);

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
