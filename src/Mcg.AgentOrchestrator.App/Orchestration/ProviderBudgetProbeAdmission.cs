using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class ProviderBudgetProbeAdmission
{
    internal static bool WaitsOnBinding(WorkerSubscriptionPreflightResult result, ProviderBudgetExhaustionHold hold) =>
        SoleHoldBlocker(result)?.BindingIdentity == hold.BindingIdentity;

    private static ProviderBudgetExhaustionHold? SoleHoldBlocker(WorkerSubscriptionPreflightResult result) =>
        result.Findings.Count(finding => finding.StartsWith("blocked:", StringComparison.OrdinalIgnoreCase)) == 1
            ? result.BlockedProviderHold : null;

    internal static void Apply(AgentOrchestratorKernel kernel, Goal goal, IReadOnlyList<TaskId> batch,
        Func<Goal, TaskSpec, IEnumerable<Goal>, WorkerSubscriptionPreflightResult> preflight, DateTimeOffset now)
    {
        var admitted = new HashSet<SubscriptionLimitEvidence.ProviderBindingKey>();
        foreach (var taskId in batch)
        {
            var task = kernel.GetTask(goal.Id, taskId);
            if (task.Status != WorkTaskStatus.Assigned) continue;
            var scope = kernel.ProviderHoldScope;
            var hold = SoleHoldBlocker(preflight(goal, task, scope));
            if (hold is null || admitted.Contains(hold.BindingIdentity) ||
                hold.Reason == ProviderFailureKind.AuthenticationRequired) continue;
            var schedule = ProviderBudgetHoldSchedules.Evaluate(scope, hold, now);
            if (now < schedule.NextProbeAt || schedule.ProbeInFlight || schedule.AdmittedTaskId is not null) continue;
            kernel.RecordTaskNote(goal.Id, task.Id, ProviderBudgetHoldSchedules.FormatProbe(
                new(hold.BindingKey, schedule.NextProbeAt, schedule.Attempt, schedule.ResetSource)
                {
                    ResetIntentId = schedule.ResetIntentId, HoldPlacedAt = hold.PlacedAt,
                    BindingIdentity = hold.BindingIdentity
                }));
            admitted.Add(hold.BindingIdentity);
        }
    }
}
