using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Owns manifest scoping for goal evidence and recovery plans on foreign targets.
internal static class GoalTargetImpactPlanScope
{
    internal static RepositoryTestImpactPlan Plan(
        RepositoryChangeSummary changeSummary, string? worktreePath, WorkerTargetHome? targetHome)
    {
        if (string.IsNullOrWhiteSpace(worktreePath))
        {
            var rootlessPlan = RepositoryTestImpactPlanner.Plan(changeSummary);
            return targetHome is null || targetHome.IsOrchestratorHome || !rootlessPlan.RequiresBuild
                ? rootlessPlan
                : Scope(rootlessPlan, string.Empty, string.Empty);
        }

        var plan = RepositoryTestImpactPlanner.Plan(changeSummary, worktreePath);
        return targetHome is null || targetHome.IsOrchestratorHome
            ? plan
            : Scope(plan, worktreePath, WorkerManifestTestProjectReader.ResolvePath(worktreePath, targetHome));
    }

    internal static RepositoryTestImpactPlan Scope(
        RepositoryTestImpactPlan plan, string worktreePath, string manifestPath)
    {
        var checks = WorkerManifestTestCheckReader.Read(manifestPath);
        var displayPath = string.IsNullOrWhiteSpace(manifestPath)
            ? "(unspecified)"
            : WorkerAcceptanceManifestDisplayPath.Render(worktreePath, manifestPath);
        if (checks is null)
        {
            var availability = File.Exists(manifestPath) ? "unreadable" : "missing";
            return plan with
            {
                RequiresBuild = true,
                Checks = [],
                Summary = $"acceptance manifest is {availability} at {displayPath}; no project checks derived"
            };
        }

        var scoped = checks.Filter(plan);
        if (scoped.Checks.Count > 0)
        {
            return scoped with
            {
                Summary = $"{plan.Summary}; {checks.ScopedSummary(displayPath)}"
            };
        }

        if (checks.Checks.Count == 0)
        {
            return scoped with
            {
                Summary = checks.NoChecksSummary(displayPath)
            };
        }

        return plan with
        {
            RequiresBuild = true,
            Checks = checks.Checks.Select(check => check.ToImpactCheck(displayPath)).ToArray(),
            Summary = checks.FallbackSummary(displayPath)
        };
    }
}
