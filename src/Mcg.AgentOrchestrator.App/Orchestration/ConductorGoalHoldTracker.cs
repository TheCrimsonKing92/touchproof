using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using static Mcg.AgentOrchestrator.App.Orchestration.ConductorBatchLoop;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Owns which outcomes persist a hold and which execution owners track their own progress.
internal static class ConductorGoalHoldTracker
{
    internal static void TrackGoalOutcome(
        AgentOrchestratorKernel kernel,
        ConductorDriver driver,
        Goal goal,
        ConductorAdvanceOutcome outcome,
        DateTimeOffset observedAt,
        TimeSpan stallThreshold,
        HashSet<GoalId> changedGoalIds,
        List<string> tickLines,
        ConductEventLogWriter? conductEventLogWriter = null)
    {
        if (goal.CurrentHold?.State == "environment-hold") { changedGoalIds.Add(goal.Id); return; }
        ConductorLandingBlockerRecorder.Observe(kernel, goal, outcome, observedAt, changedGoalIds);
        ConductorGateAttention.ReconcileOutcome(kernel, driver, goal, outcome);
        if (outcome is ConductorAdvanceOutcome.Held held)
        {
            if (held.OperatorEvidenceConsistency is { } consistency)
            {
                TrackGoalHold(kernel, goal, PlannerOperatorEvidenceConsistencyHold.State, consistency.Render(),
                    observedAt, TimeSpan.MaxValue, changedGoalIds, tickLines, consistency.Identity, driver, conductEventLogWriter);
                return;
            }

            if (driver.SliceBatchParentExecutionGuard?.IsSliceBatchParent(goal) == true)
            {
                ClearGoalHold(kernel, goal, changedGoalIds);
                return;
            }

            if (held.State is GoalLifecycleState.Running
                or GoalLifecycleState.AwaitingVerification
                or GoalLifecycleState.Verifying)
            {
                ClearGoalHold(kernel, goal, changedGoalIds);
                return;
            }

            if (held.Owner is ConductorHoldOwner.BackgroundAttempt or ConductorHoldOwner.AcceptanceQueue
                or ConductorHoldOwner.DurableOutbox or ConductorHoldOwner.WorkerCapacity)
            {
                ClearGoalHold(kernel, goal, changedGoalIds);
                return;
            }

            // The pull request tracker needs the hold record, but owns its progress so it never stalls.
            TrackGoalHold(
                kernel, goal,
                held.State.ToString(),
                TrainEjectionReport.DescribeWaitingHold(driver, goal.Id, held, held.Reason),
                observedAt,
                held.Owner is ConductorHoldOwner.PullRequestReview ? TimeSpan.MaxValue : stallThreshold,
                changedGoalIds,
                tickLines,
                held.StableIdentity ?? TrainEjectionReport.OriginalHoldReason(held),
                driver,
                conductEventLogWriter);
            return;
        }

        ClearGoalHold(kernel, goal, changedGoalIds);
    }
}
