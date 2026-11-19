using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class ProviderBudgetProbeAdmission
{
    internal static bool WaitsOnBinding(WorkerSubscriptionPreflightResult result, string binding)
    {
        var blocked = result.Findings.Where(finding => finding.StartsWith("blocked:", StringComparison.OrdinalIgnoreCase)).ToArray();
        return blocked.Length == 1 && blocked[0].StartsWith("blocked: code=provider-budget-exhausted;", StringComparison.Ordinal) &&
            blocked[0].Contains($"binding {binding} (", StringComparison.OrdinalIgnoreCase);
    }

    internal static void Apply(AgentOrchestratorKernel kernel, Goal goal, IReadOnlyList<TaskId> batch,
        Func<Goal, TaskSpec, IEnumerable<Goal>, WorkerSubscriptionPreflightResult> preflight, DateTimeOffset now)
    {
        var admitted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var taskId in batch)
        {
            var task = kernel.GetTask(goal.Id, taskId);
            if (task.Status != WorkTaskStatus.Assigned) continue;
            var blocked = preflight(goal, task, kernel.ProviderHoldScope).Findings
                .Where(finding => finding.StartsWith("blocked:", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (blocked.Length != 1 || !blocked[0].StartsWith("blocked: code=provider-budget-exhausted;", StringComparison.Ordinal)) continue;
            // The binding is derived from dispatch history; the diagnostic only identifies which hold blocked this task.
            var scope = kernel.ProviderHoldScope;
            foreach (var binding in ProviderHoldBinding.Candidates(scope))
            {
                if (admitted.Contains(binding.Key) || !blocked[0].Contains($"binding {binding.Key} (", StringComparison.OrdinalIgnoreCase) ||
                    !ProviderLimitSignals.TryGetExactHold(scope, binding, out var hold) ||
                    hold.Reason == ProviderFailureKind.AuthenticationRequired) continue;
                var schedule = ProviderBudgetHoldSchedules.Evaluate(kernel.ProviderHoldScope, hold, now);
                if (now < schedule.NextProbeAt || schedule.ProbeInFlight || schedule.AdmittedTaskId is not null) break;
                kernel.RecordTaskNote(goal.Id, task.Id, ProviderBudgetHoldSchedules.FormatProbe(
                    new(hold.BindingKey, schedule.NextProbeAt, schedule.Attempt, schedule.ResetSource)
                        { ResetIntentId = schedule.ResetIntentId, HoldPlacedAt = hold.PlacedAt }));
                admitted.Add(hold.BindingKey);
                break;
            }
        }
    }
}
