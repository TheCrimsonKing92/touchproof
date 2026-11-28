using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Owns manifest scoping for goal evidence and recovery plans on foreign targets.
internal static class GoalTargetImpactPlanScope
{
    internal static PreReviewEvidenceContext BuildEvidenceContext(Goal goal, string executionDirectory,
        string integrationBranch, WorkerTargetHome? targetHome, string? orchestratorDirectory, TickTestImpactPlanMemo? memo)
    {
        var worktreePath = GoalWorktrees.TryResolve(executionDirectory, goal.Id);
        var candidateSha = worktreePath is null ? null : ConductorDriver.TryResolveGitHead(worktreePath);
        var changedFiles = worktreePath is null
            ? Array.Empty<string>()
            : GoalAcceptanceEvidenceBundleBuilder.GetChangedFiles(worktreePath, integrationBranch);
        var candidate = memo is not null && worktreePath is not null && candidateSha is not null
            ? TickTestImpactPlanMemo.Candidate.Read(worktreePath, integrationBranch) : null;
        Func<RepositoryChangeSummary, string, RepositoryTestImpactPlan>? plan = candidate is not null && candidate.Head == candidateSha
            ? (summary, _) => memo!.Plan(candidate, summary) : null;
        var evidenceDirectory = orchestratorDirectory ?? targetHome?.ProjectHomeDirectory ??
            OrchestratorWorkspace.ForDirectory(executionDirectory).OrchestratorDirectory;
        return BuildEvidenceContext(candidateSha, changedFiles, worktreePath, targetHome, evidenceDirectory, goal, plan);
    }

    internal static PreReviewEvidenceContext BuildEvidenceContext(
        string? candidateSha, IReadOnlyList<string> changedFiles, string? repositoryRoot, WorkerTargetHome? targetHome,
        string? orchestratorDirectory, Goal? goal, Func<RepositoryChangeSummary, string, RepositoryTestImpactPlan>? rawPlanner = null)
    {
        var changeSummary = RepositoryChangeClassifier.Classify(changedFiles);
        var boundedPlan = ReverseDependencyBreadthBound.Apply(
            Plan(changeSummary, repositoryRoot, targetHome, rawPlanner), changeSummary.Files.Select(file => file.Path));
        var plan = ReverseDependencyEvidenceCostGate.Apply(boundedPlan, selections =>
            ReverseDependencyEvidenceCostHistory.Read(repositoryRoot, orchestratorDirectory, selections, changedFiles, targetHome?.ProjectHomeDirectory));
        return PreReviewEvidenceContextBuilder.Build(candidateSha, changeSummary, plan, goal: goal, worktreePath: repositoryRoot);
    }

    internal static RepositoryTestImpactPlan Plan(
        RepositoryChangeSummary changeSummary, string? worktreePath, WorkerTargetHome? targetHome,
        Func<RepositoryChangeSummary, string, RepositoryTestImpactPlan>? rawPlanner = null)
    {
        if (string.IsNullOrWhiteSpace(worktreePath))
        {
            var rootlessPlan = RepositoryTestImpactPlanner.Plan(changeSummary);
            return targetHome is null || targetHome.IsOrchestratorHome || !rootlessPlan.RequiresBuild
                ? rootlessPlan
                : Scope(rootlessPlan, string.Empty, string.Empty);
        }

        var plan = (rawPlanner ?? RepositoryTestImpactPlanner.Plan)(changeSummary, worktreePath);
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
