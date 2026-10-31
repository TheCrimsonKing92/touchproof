using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class ReviewFindingRetryRouteResolver
{
    internal static ReviewRetryRoute? Resolve(Goal goal, TaskSpec reviewerTask, string blockerProse)
    {
        try
        {
            var findings = AutoReviewRetryConvergenceBriefBuilder
                .ReadStructuredReviewFindingState(goal, reviewerTask)
                .Where(finding => finding.State == ReviewFindingState.Open &&
                    finding.Severity == FindingSeverity.Blocking)
                .ToArray();
            return reviewerTask.RequiredRole == AgentRole.Tester &&
                (findings.Length == 0 || findings.Any(finding => finding.Category == FindingCategory.Unspecified))
                ? null : ReviewFindingRouting.Resolve(findings, blockerProse);
        }
        catch (Exception ex) when (
            ex is ReviewFindingConvergenceException or InvalidOperationException or ArgumentException)
        {
            return reviewerTask.RequiredRole == AgentRole.Tester ? null : ReviewFindingRouting.Resolve([], blockerProse);
        }
    }
}
