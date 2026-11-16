using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Derives coverage from the retained gate failure; never runs or records evidence.
internal static class FailedGateFocusedClasses
{
    internal static IReadOnlyList<(string Alias, string ClassName)> Read(
        Goal goal, Func<string, ConductorParallelAcceptanceAttempt?> readLatestGateAttempt)
    {
        var failure = goal.RetainedAcceptanceFailure;
        if (failure is null) return [];
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
                    return FromIdentities(summary.RequiredUnmetCriteria.SelectMany(check =>
                        (check.FailingTestIdentities ?? []).Select(identity => (check.TestProjectPath, identity))));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or JsonException)
        {
            // An unreadable artifact cannot supply identities. Attribution may still name this failure's tests.
        }

        return FromIdentities((AttributedFailureDeveloperRoute.ReadAcceptanceFailure(failure)?.FailingTests ?? [])
            .Select(identity => ((string?)null, identity)));
    }

    internal static IReadOnlyList<(string Alias, string ClassName)> FromIdentities(
        IEnumerable<(string? Project, string Identity)> identities)
    {
        var classes = new HashSet<(string Alias, string ClassName)>();
        foreach (var (project, identity) in identities)
        {
            if (string.IsNullOrWhiteSpace(identity)) continue;
            var className = AcceptanceTestSourceResolver.ExtractClassName(identity)?.Split('+')[0];
            if (className is null || !PreReviewFocusedRequestSplitter.IsPositiveClause($"FullyQualifiedName~{className}"))
                continue;
            if (PreReviewFocusedRequestSplitter.TryResolveBrokerAlias(project ?? identity, out var alias))
                classes.Add((alias!, className));
        }
        return classes.OrderBy(item => item.Alias, StringComparer.Ordinal)
            .ThenBy(item => item.ClassName, StringComparer.Ordinal).ToArray();
    }
}
