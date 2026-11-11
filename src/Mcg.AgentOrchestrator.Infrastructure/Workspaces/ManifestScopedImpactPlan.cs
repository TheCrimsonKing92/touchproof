using Mcg.AgentOrchestrator.Core;
using AcceptanceManifestCheck = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.AcceptanceManifestCheck;

namespace Mcg.AgentOrchestrator.Infrastructure;

/// <summary>Scopes gate impact checks to the loaded manifest's declared test checks.</summary>
internal static class ManifestScopedImpactPlan
{
    internal static RepositoryTestImpactPlan? PlanChangeScoped(
        RepositoryChangeSummary summary,
        string worktreePath,
        IReadOnlyList<AcceptanceManifestCheck> manifestChecks)
    {
        var plan = Plan(summary, worktreePath, manifestChecks);
        return !plan.RequiresBuild ||
            plan.Checks.Count == 0 ||
            plan.RequiresBroadVerification ||
            plan.Checks.Any(check => check.Command.Count == 0)
            ? null
            : plan;
    }

    internal static RepositoryTestImpactPlan Plan(
        IEnumerable<string> changedFiles,
        string worktreePath,
        IReadOnlyList<AcceptanceManifestCheck> manifestChecks) =>
        Plan(RepositoryChangeClassifier.Classify(changedFiles), worktreePath, manifestChecks);

    internal static RepositoryTestImpactPlan Plan(
        RepositoryChangeSummary summary,
        string worktreePath,
        IReadOnlyList<AcceptanceManifestCheck> manifestChecks)
    {
        return Scope(RepositoryTestImpactPlanner.Plan(summary, worktreePath), manifestChecks);
    }

    internal static RepositoryTestImpactPlan Scope(
        RepositoryTestImpactPlan plan, IReadOnlyList<AcceptanceManifestCheck> manifestChecks)
    {
        var declared = new ManifestDeclaredTestCheckSet(manifestChecks
            .Where(check => ManifestTestCheckKinds.Canonicalize(check.Type) is not null)
            .Select(check => new ManifestDeclaredTestCheck(check.Name,
                ManifestTestCheckKinds.Canonicalize(check.Type)!, check.Project, check.Arguments)));

        // Built-in declarations retain this repository's existing planning policy.
        // No-build plans carry non-executable evidence rather than project checks.
        return declared.DotnetProjects.DeclaresAnyBuiltInTestProject || !plan.RequiresBuild
            ? plan
            : declared.Filter(plan);
    }
}
