namespace Mcg.AgentOrchestrator.Core;

// Mutation results carry the visible status, including explicit-disposition overrides.
internal readonly record struct GoalStatusChange(GoalId GoalId, GoalStatus From, GoalStatus To);

internal static class GoalStatusTransitionRecorder
{
    internal const string GoalLifecycleActivateGoal = "GoalLifecycle.ActivateGoal";
    internal const string GoalLifecycleAddTask = "GoalLifecycle.AddTask";
    internal const string GoalLifecycleReopenVerifyingGoalAfterAcceptanceAttemptInvalidated = "GoalLifecycle.ReopenVerifyingGoalAfterAcceptanceAttemptInvalidated";
    internal const string GoalLifecycleNormalizePrematureCompletedGoalForAcceptance = "GoalLifecycle.NormalizePrematureCompletedGoalForAcceptance";
    internal const string GoalLifecycleReconcileGoalVerificationStatus = "GoalLifecycle.ReconcileGoalVerificationStatus";
    internal const string GoalLifecycleBeginGoalAcceptanceVerification = "GoalLifecycle.BeginGoalAcceptanceVerification";
    internal const string GoalLifecycleReconcileGoalAcceptanceVerified = "GoalLifecycle.ReconcileGoalAcceptanceVerified";
    internal const string GoalLifecycleReconcileGoalAcceptanceFailed = "GoalLifecycle.ReconcileGoalAcceptanceFailed";
    internal const string GoalLifecycleRouteRecordedAcceptanceFailure = "GoalLifecycle.RouteRecordedAcceptanceFailure";
    internal const string GoalLifecycleRestoreVerifiedForClassifiedAcceptanceFailure = "GoalLifecycle.RestoreVerifiedForClassifiedAcceptanceFailure";
    internal const string GoalLifecycleRestoreVerifiedForSupersededAcceptanceFailure = "GoalLifecycle.RestoreVerifiedForSupersededAcceptanceFailure";
    internal const string GoalLifecycleRetryAcceptanceGate = "GoalLifecycle.RetryAcceptanceGate";
    internal const string GoalLifecycleUnparkGoal = "GoalLifecycle.UnparkGoal";
    internal const string GoalLifecycleCompleteGoal = "GoalLifecycle.CompleteGoal";
    internal const string GoalLifecycleCompleteGoalFromMergeEvidence = "GoalLifecycle.CompleteGoalFromMergeEvidence";
    internal const string GoalLifecycleStopGoal = "GoalLifecycle.StopGoal";
    internal const string GoalLifecycleEscalateTaskFailure = "GoalLifecycle.EscalateTaskFailure";
    internal const string GoalLifecycleReopenTerminalGoalWithNonTerminalTasks = "GoalLifecycle.ReopenTerminalGoalWithNonTerminalTasks";
    internal const string GoalLifecycleReopenAcceptanceFailedGoalWithRetry = "GoalLifecycle.ReopenAcceptanceFailedGoalWithRetry";
    internal const string GoalLifecycleHoldTaskForExistingHumanInput = "GoalLifecycle.HoldTaskForExistingHumanInput";
    internal const string GoalLifecycleSweepParkedGoalHumanWaits = "GoalLifecycle.SweepParkedGoalHumanWaits";
    internal const string InternalStateRefreshGoalStatusBlockingHumanWait = "InternalState.RefreshGoalStatus.BlockingHumanWait";
    internal const string InternalStateRefreshGoalStatusAllGatesPassed = "InternalState.RefreshGoalStatus.AllGatesPassed";
    internal const string InternalStateRefreshGoalStatusResumeActive = "InternalState.RefreshGoalStatus.ResumeActive";
    internal const string RecordingRecordTaskDispatch = "Recording.RecordTaskDispatch";
    internal const string RecordingRecordTaskProcessStarted = "Recording.RecordTaskProcessStarted";
    internal const string DefaultTasksConcludeInterruptedDispatchRecovery = "DefaultTasks.ConcludeInterruptedDispatchRecovery";
    internal const string TaskGraphRepairApply = "TaskGraphRepair.Apply";
    internal const string ApplyExplicitDispositionPark = "ApplyExplicitDisposition.Park";
    internal const string ApplyExplicitDispositionUnpark = "ApplyExplicitDisposition.Unpark";

    internal static void Record(IGoalLifecycleEventWriter writer, GoalStatusChange change, string site)
    {
        if (change.From != change.To)
            writer.AppendGoalStatusTransition(change.GoalId, change.From, change.To, site);
    }
}
