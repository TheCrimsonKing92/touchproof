using Mcg.AgentOrchestrator.Core;
using AcceptanceManifestCheck = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.AcceptanceManifestCheck;

namespace Mcg.AgentOrchestrator.Infrastructure;

/// <summary>Scopes gate impact checks to the loaded manifest's declared test checks.</summary>
internal static class ManifestScopedImpactPlan
{
    internal static VerificationPolicy CompileRequiredPolicy(string worktreePath,
        IReadOnlyList<string> changedFiles, IReadOnlyList<AcceptanceManifestCheck> manifestChecks,
        SelectionSourceTexts? sourceTexts) => VerificationPolicyCompiler.Compile(
            AgentRole.Reviewer, goalObjective: string.Empty, taskDescription: string.Empty,
            verificationPlan: null, changedFiles, Plan(changedFiles, worktreePath, manifestChecks, sourceTexts));

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
        IReadOnlyList<AcceptanceManifestCheck> manifestChecks, SelectionSourceTexts? sourceTexts = null) =>
        Plan(RepositoryChangeClassifier.Classify(changedFiles), worktreePath, manifestChecks, sourceTexts);

    internal static RepositoryTestImpactPlan Plan(
        RepositoryChangeSummary summary,
        string worktreePath,
        IReadOnlyList<AcceptanceManifestCheck> manifestChecks, SelectionSourceTexts? sourceTexts = null)
    {
        return Scope(RepositoryTestImpactPlanner.Plan(summary, worktreePath, sourceTexts), manifestChecks);
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
