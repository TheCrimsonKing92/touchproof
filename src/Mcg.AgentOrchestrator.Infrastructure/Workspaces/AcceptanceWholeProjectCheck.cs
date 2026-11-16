using Check = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.AcceptanceManifestCheck;
using static Mcg.AgentOrchestrator.Infrastructure.AcceptancePolicyShardPlanner;

namespace Mcg.AgentOrchestrator.Infrastructure;

// Coverage is restricted to project checks retained by the gate's scheduling predicates.
internal sealed record AcceptanceWholeProjectCheck(string Name, string Project)
{
    internal static IReadOnlyList<AcceptanceWholeProjectCheck> Scheduled(
        IReadOnlyList<Check> manifestChecks, IReadOnlyList<Check> requiredChecks,
        PolicyShardPlan policy, DotnetShardDisposition disposition, IReadOnlyList<string>? changedFiles)
    {
        if (disposition != DotnetShardDisposition.RunDotnetShards || manifestChecks.Concat(requiredChecks).Any(check =>
            check.Type.Equals("dotnet-test", StringComparison.OrdinalIgnoreCase) &&
            (string.IsNullOrWhiteSpace(check.Project) || check.Project.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)))) return [];
        // All inputs are already materialized; this uses the scheduler without reading the tree again.
        return GoalAcceptanceVerifier.BuildPolicyEffectiveChecks(string.Empty, manifestChecks, changedFiles,
                requiredChecks, policy, [])
            .Where(check => check.Type.Equals("dotnet-test", StringComparison.OrdinalIgnoreCase) &&
                check.Project?.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) == true &&
                !IsFocusedProjectDotnetCheck(check) && !IsBroadInfrastructureTestCheck(check))
            .Select(check => new AcceptanceWholeProjectCheck(check.Name, check.Project!)).Distinct().ToArray();
    }
}
