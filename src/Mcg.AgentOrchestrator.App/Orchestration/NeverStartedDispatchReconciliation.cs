using Mcg.AgentOrchestrator.App.Application;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

/// <summary>Reconciles recorded start failures for which no worker start has been claimed.</summary>
internal static class NeverStartedDispatchReconciliation
{
    internal static bool IsUnclaimed(TaskSpec task) => task.Status == WorkTaskStatus.Running &&
        task.LastDispatch is { } dispatch && task.LastProcess is null &&
        !task.RetryAdmissionHistory.Any(receipt => receipt.LinkedDispatchAt == dispatch.DispatchedAt &&
            receipt.WorkerStartClaimedAt is not null);

    internal static bool CanRecover(Goal goal, TaskSpec task) =>
        IsUnclaimed(task) && DispatchStartFailureEvidence.IsRecorded(goal, task);

    internal static ConductorAdvanceResult? BeforeAdvance(AgentOrchestratorKernel? kernel, Goal goal,
        GoalLifecycleState state, ConductorAutonomyPolicy policy, Func<string, ConductorAdvanceResult> soloHold,
        Func<TaskSpec, bool>? isPreparedDispatchHeld = null)
    {
        if (kernel is not null && goal.Status == GoalStatus.Active)
        {
            var recovered = goal.Tasks.Where(task => CanRecover(goal, task) &&
                isPreparedDispatchHeld?.Invoke(task) != true).ToArray();
            foreach (var task in recovered)
                kernel.DiscardUnstartedPreparedDispatch(goal.Id, task.Id, task.LastDispatch!.DispatchedAt,
                    "Conductor reconciled a never-started dispatch: no process or current-dispatch start claim was recorded.");
            if (recovered.Length > 0)
                return Hold(goal, state, policy, "never-started-dispatch-reconciled",
                    $"Returned {recovered.Length} never-started dispatch(es) to assignment; dispatch may resume next tick.");
        }

        if (goal.Tasks.FirstOrDefault(task => AbortedDispatchStartRecovery.IsRepeated(goal, task.Id)) is { } repeated)
            return GoalDispatchOperations.IsSoloCorrection(repeated)
                ? soloHold(AbortedDispatchStartRecovery.RepeatedReason)
                : Hold(goal, state, policy, AbortedDispatchStartRecovery.RepeatedReason, AbortedDispatchStartRecovery.DescribeRepeated());
        return goal.Tasks.Any(GoalDispatchOperations.HasUncertainSoloCorrectionStart)
            ? soloHold("solo-start-claimed-reconciliation-required") : null;
    }

    private static ConductorAdvanceResult Hold(Goal goal, GoalLifecycleState state,
        ConductorAutonomyPolicy policy, string reason, string detail) =>
        new(goal.Id.Value, goal.Id.Value[..8], policy.Name,
            new ConductorAdvanceOutcome.Held(state, detail)
            {
                Decision = new("dispatch-start-recovery", "Hold", 1, reason, detail,
                    [new("goalId", goal.Id.Value)])
            });

    internal static (int ProcessId, DateTimeOffset StartedAt) CancellationIdentity(Goal goal, TaskSpec task, TaskProcessRecord? process)
    {
        if (process is not null) return (process.ProcessId, process.StartedAt);
        if (CanRecover(goal, task)) return (0, task.LastDispatch!.DispatchedAt);
        throw new InvalidOperationException($"Task '{task.Id}' has no background process to cancel.");
    }

    internal static bool ApplyCancel(AgentOrchestratorKernel kernel, Goal goal, TaskSpec task,
        CancelDispatchOperatorIntentPayload payload)
    {
        if (!CanRecover(goal, task)) return false;
        var dispatch = task.LastDispatch!;
        if (payload.DispatchId != BackgroundDispatchRunner.BuildDispatchId(goal.Id, task.Id, dispatch) ||
            payload.ProcessId != 0 || payload.ProcessStartedAt != dispatch.DispatchedAt)
            throw new InvalidOperationException("dispatch-mismatch: cancel-dispatch intent does not name the current never-started dispatch.");
        kernel.DiscardUnstartedPreparedDispatch(goal.Id, task.Id, dispatch.DispatchedAt,
            "Operator cancel-dispatch reconciled a never-started dispatch: no process or current-dispatch start claim was recorded.");
        return true;
    }
}
