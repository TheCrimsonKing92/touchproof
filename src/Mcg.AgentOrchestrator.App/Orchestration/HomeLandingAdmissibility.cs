using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

/// <summary>Checks the home landing's pending-input backstop before tuple mutation begins.</summary>
internal static class HomeLandingAdmissibility
{
    internal static string? Evaluate(AgentOrchestratorKernel? kernel, Goal goal, OrchestratorWorkspace? workspace)
    {
        if (kernel is null || workspace is null) return null;

        var revision = LandingExecutor.GitRunner(workspace.ExecutionDirectory,
            ["rev-parse", "refs/heads/" + GoalWorktrees.BranchName(goal.Id)]);
        if (!revision.Succeeded || revision.DrainTimedOut || string.IsNullOrWhiteSpace(revision.Output))
            return "home candidate revision unresolved: " + revision.Error;

        return LandingExecutor.EvaluatePendingHumanInput(kernel, goal, workspace, revision.Output.Trim()).HoldDescription;
    }
}
