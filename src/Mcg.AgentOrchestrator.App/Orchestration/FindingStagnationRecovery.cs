using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class FindingStagnationRecovery
{
    internal static bool TryObserve(Goal goal, TaskSpec triggeringTask, AgentOrchestratorKernel? kernel,
        out FailedGoalFindingObservation observation)
    {
        observation = FailedGoalFindingObservation.None;
        if (triggeringTask.LastVerification is null) return false;
        var findings = ReadRoutedFindings(goal, triggeringTask);
        if (kernel is not null) FindingStagnationDecisionRecorder.RecordSources(kernel, goal, triggeringTask, findings);
        var decisions = FindingStagnationTracker.Evaluate(goal, kernel?.HumanInputRequests ?? [], triggeringTask, findings);
        if (decisions.Count == 0) return false;
        foreach (var decision in decisions)
        {
            if (kernel is not null) FindingStagnationDecisionRecorder.Record(kernel, decision);
        }
        observation = FailedGoalFindingObservation.Observed(
            FailedGoalFindingObservationKind.FindingStagnationDecisionRecorded,
            string.Join(Environment.NewLine, decisions.Select(decision => decision.Question)));
        return true;
    }

    internal static IReadOnlyList<ReviewFinding> ReadRoutedFindings(Goal goal, TaskSpec triggeringTask) =>
        new[] { AgentRole.Reviewer, AgentRole.Tester }.SelectMany(role =>
            role == triggeringTask.RequiredRole
                ? AutoReviewRetryConvergenceBriefBuilder.ReadStructuredReviewFindingState(goal, triggeringTask)
                : AutoReviewRetryConvergenceBriefBuilder.ReadStructuredReviewFindingState(goal, role, triggeringTask.LastVerification!.CompletedAt))
            .Where(finding => finding.State == ReviewFindingState.Open && finding.Severity == FindingSeverity.Blocking).ToArray();
}
