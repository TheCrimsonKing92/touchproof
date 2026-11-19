using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using static ConductorDriverTests;

internal static class ReviewerFindingEvidenceDeliveryFixture
{
    internal static (AgentOrchestratorKernel Kernel, Goal Goal, TaskSpec Reviewer) FailedReviewer(string blocker, ReviewFinding finding)
    {
        var (kernel, goal) = SoftwareGoal();
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.Where(task => task.RequiredRole != AgentRole.Reviewer))
        {
            PassVerification(kernel, goal, task);
        }

        FailReviewerNeedsWork(kernel, goal, reviewer, blocker, findings: [finding]);
        return (kernel, goal, reviewer);
    }

    internal static FocusedEvidenceRunResult LegacyGreen(string request) =>
        new(
            request,
            Accepted: true,
            Passed: true,
            Summary: "focused evidence passed: 1 check; receipts: C:\\tmp\\trx",
            Checks:
            [
                new AcceptanceCheckResult(
                    "reviewer focused evidence: Infrastructure.Tests FullyQualifiedName~ConductorDriverTests",
                    true,
                    0,
                    null,
                    ArtifactsPath: "C:\\tmp\\trx")
            ]);

    internal static FocusedEvidenceRunResult LegacyFailure(string request) =>
        new(
            request,
            Accepted: true,
            Passed: false,
            Summary: "focused evidence failed; receipts: C:\\tmp\\failed-derived-trx",
            Checks: []);
}
