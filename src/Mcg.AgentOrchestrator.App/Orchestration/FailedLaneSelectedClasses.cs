using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class FailedLaneSelectedClasses
{
    internal sealed record Lane(string Name, IReadOnlyList<(string Alias, string ClassName)> Classes);

    internal static IReadOnlyList<Lane> Read(
        ConductorParallelAcceptanceAttempt attempt, IReadOnlyList<AcceptanceCheckResult> requiredUnmetChecks)
    {
        if (requiredUnmetChecks.Count == 0 || string.IsNullOrWhiteSpace(attempt.ResultPath) ||
            OwnerConsoleGateLanePlanReader.ArtifactPath(attempt.ResultPath, attempt.AttemptId) is not { } path ||
            new OwnerConsoleGateLanePlanReader().Read(path, attempt.AttemptId)?.SelectedExecution?.Lanes is not { Count: > 0 } lanes ||
            lanes.Any(lane => lane is null || string.IsNullOrWhiteSpace(lane.Name) || lane.SelectedClasses is null))
            return [];

        var resolver = new OwnerConsoleGateLaneResolver(lanes.Select(lane => new OwnerConsoleGateManifestLane(lane.Name, 0)).ToArray());
        var checksByLane = requiredUnmetChecks.Where(check => !string.IsNullOrWhiteSpace(check.Name))
            .ToLookup(check => resolver.Resolve(check.Name), StringComparer.Ordinal);
        return lanes.Where(lane => checksByLane.Contains(lane.Name))
            .OrderBy(lane => lane.Name, StringComparer.Ordinal)
            .Select(lane => new Lane(lane.Name, ReadClasses(lane, checksByLane[lane.Name]))).ToArray();
    }

    private static IReadOnlyList<(string Alias, string ClassName)> ReadClasses(
        AcceptanceSelectedExecutionLane lane, IEnumerable<AcceptanceCheckResult> checks)
    {
        var project = checks.Select(check => check.TestProjectPath).FirstOrDefault(path =>
            path is not null && PreReviewFocusedRequestSplitter.TryResolveBrokerAlias(path, out _));
        return FailedGateFocusedClasses.FromClassNames(lane.SelectedClasses
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name =>
            {
                var outerName = name.Split('+')[0];
                return (project ?? name, (string?)outerName[(outerName.LastIndexOf('.') + 1)..]);
            }));
    }

    internal static IReadOnlyList<string> AppendFeedback(GoalId goalId, AcceptanceVerificationSummary acceptance,
        ConductorParallelAcceptanceAttemptCoordinator attempts, IReadOnlyList<string> feedback)
    {
        if (string.IsNullOrWhiteSpace(acceptance.AttemptId)) return feedback;
        try
        {
            if (attempts.TryReadLatest(goalId.Value) is not { } attempt || attempt.GoalId != goalId.Value ||
                attempt.AttemptId != acceptance.AttemptId || attempt.Kind != ConductorParallelAcceptanceAttemptCoordinator.GateDispatchKind)
                return feedback;

            var lanes = Read(attempt, acceptance.RequiredUnmetCriteria);
            return lanes.Count == 0 ? feedback : [.. feedback, .. lanes.Select(FormatFeedback)];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or
            NotSupportedException or System.Security.SecurityException)
        {
            // Missing lane feedback must not prevent a retry.
            return feedback;
        }
    }

    private static string FormatFeedback(Lane lane) =>
        $"Failed lane '{lane.Name}' selected classes: " + (lane.Classes.Count == 0 ? "(none)" :
            string.Join(", ", lane.Classes.Select(item => $"{item.Alias}/{item.ClassName}")));
}
