using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Derives coverage from the retained gate failure; never runs or records evidence.
internal static class FailedGateFocusedClasses
{
    internal static IReadOnlyList<(string Alias, string ClassName)> Read(
        Goal goal, Func<string, ConductorParallelAcceptanceAttempt?> readLatestGateAttempt, string? executionDirectory = null)
    {
        var failure = goal.RetainedAcceptanceFailure;
        if (failure is null) return [];
        var worktreePath = executionDirectory is null ? null : GoalWorktrees.TryResolve(executionDirectory, goal.Id);
        try
        {
            if (!string.IsNullOrWhiteSpace(failure.AttemptId) &&
                readLatestGateAttempt(goal.Id.Value) is { } attempt &&
                attempt.GoalId == goal.Id.Value && attempt.AttemptId == failure.AttemptId &&
                attempt.Kind == ConductorParallelAcceptanceAttemptCoordinator.GateDispatchKind &&
                attempt.BranchHeadSha == failure.BranchHeadSha && attempt.MainHeadSha == failure.MainHeadSha)
            {
                var artifact = JsonSerializer.Deserialize<ConductorParallelAcceptanceRunArtifact>(
                    File.ReadAllText(attempt.ResultPath), new JsonSerializerOptions(JsonSerializerDefaults.Web));
                if (artifact is { Acceptance: { Passed: false } summary } &&
                    summary.UnmetCriteria is { } checks && checks.All(check => check is not null) &&
                    artifact.DispatchKind == ConductorParallelAcceptanceAttemptCoordinator.GateDispatchKind &&
                    summary.AttemptId == attempt.AttemptId && summary.BranchHeadSha == attempt.BranchHeadSha &&
                    summary.MainHeadSha == attempt.MainHeadSha && artifact.BranchHeadSha == attempt.BranchHeadSha &&
                    artifact.MainHeadSha == attempt.MainHeadSha)
                {
                    var requiredChecks = summary.RequiredUnmetCriteria;
                    var failed = FromIdentities(requiredChecks
                        .Where(check => !IsNestedFixtureProject(worktreePath, check.TestProjectPath))
                        .SelectMany(check => (check.FailingTestIdentities ?? [])
                            .Select(identity => (check.TestProjectPath, identity))));
                    var lanes = FailedLaneSelectedClasses.Read(attempt, requiredChecks);
                    return ExcludeNestedFixtureClasses(worktreePath, failed.Concat(lanes.SelectMany(lane => lane.Classes)));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or JsonException)
        {
            // An unreadable artifact cannot supply identities. Attribution may still name this failure's tests.
        }

        var attributed = FromIdentities((AttributedFailureDeveloperRoute.ReadAcceptanceFailure(failure)?.FailingTests ?? [])
            .Select(identity => ((string?)null, identity)));
        return ExcludeNestedFixtureClasses(worktreePath, attributed);
    }

    private static bool IsNestedFixtureProject(string? worktreePath, string? project) =>
        worktreePath is not null && project is not null &&
        project.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) &&
        PreReviewFocusedRequestSplitter.TryResolveBrokerAlias(project, out var alias) &&
        !RepositoryProjectOwnership.IsExtractedInfrastructureProject(project) &&
        RepositoryProjectOwnership.FindNestedProjectFile(worktreePath, Path.GetDirectoryName(BrokerProject(alias!))!, project) is not null;

    private static IReadOnlyList<(string Alias, string ClassName)> ExcludeNestedFixtureClasses(
        string? worktreePath, IEnumerable<(string Alias, string ClassName)> candidates) =>
        OrderDistinct(worktreePath is null ? candidates : candidates.GroupBy(item => item.Alias).SelectMany(group =>
            FocusedEvidenceNestedProjectClassSelection.ExcludeFixtureClasses(worktreePath, BrokerProject(group.Key),
                group.Select(item => item.ClassName).Distinct()).Select(className => (group.Key, className))));

    private static string BrokerProject(string alias) => alias == "Core.Tests"
        ? AcceptancePolicyShardPlanner.CoreTestsProject : AcceptancePolicyShardPlanner.InfrastructureTestsProject;

    internal static IReadOnlyList<(string Alias, string ClassName)> FromIdentities(
        IEnumerable<(string? Project, string Identity)> identities) => FromClassNames(identities
            .Where(item => !string.IsNullOrWhiteSpace(item.Identity))
            .Select(item => (item.Project ?? item.Identity, AcceptanceTestSourceResolver.ExtractClassName(item.Identity))));

    internal static IReadOnlyList<(string Alias, string ClassName)> FromClassNames(
        IEnumerable<(string AliasSource, string? ClassName)> candidates) => OrderDistinct(ResolveClasses(candidates));

    private static IEnumerable<(string Alias, string ClassName)> ResolveClasses(
        IEnumerable<(string AliasSource, string? ClassName)> candidates)
    {
        foreach (var (aliasSource, name) in candidates)
        {
            var className = name?.Split('+')[0];
            if (className is not null && PreReviewFocusedRequestSplitter.IsPositiveClause($"FullyQualifiedName~{className}") &&
                PreReviewFocusedRequestSplitter.TryResolveBrokerAlias(aliasSource, out var alias))
                yield return (alias!, className);
        }
    }

    private static IReadOnlyList<(string Alias, string ClassName)> OrderDistinct(
        IEnumerable<(string Alias, string ClassName)> classes) => classes.Distinct()
        .OrderBy(item => item.Alias, StringComparer.Ordinal)
        .ThenBy(item => item.ClassName, StringComparer.Ordinal).ToArray();
}
