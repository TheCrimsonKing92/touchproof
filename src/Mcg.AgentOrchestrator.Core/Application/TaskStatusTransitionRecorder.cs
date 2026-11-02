namespace Mcg.AgentOrchestrator.Core;

internal readonly record struct TaskStatusChange(TaskId TaskId, WorkTaskStatus From, WorkTaskStatus To);

internal static class TaskStatusTransitionRecorder
{
    internal const string GoalLifecycleActivateGoal = "GoalLifecycle.ActivateGoal";
    internal const string GoalLifecycleAddTask = "GoalLifecycle.AddTask";
    internal const string GoalLifecycleRequeueInterruptedDispatch = "GoalLifecycle.RequeueInterruptedDispatch";
    internal const string GoalLifecycleRedelegateTask = "GoalLifecycle.RedelegateTask";
    internal const string GoalLifecycleReassignTaskAgentAssign = "GoalLifecycle.ReassignTaskAgent.Assign";
    internal const string GoalLifecycleReassignTaskAgentRunning = "GoalLifecycle.ReassignTaskAgent.Running";
    internal const string GoalLifecycleAcknowledgeSubscriptionLimitReview = "GoalLifecycle.AcknowledgeSubscriptionLimitReview";
    internal const string GoalLifecycleReportTaskProgress = "GoalLifecycle.ReportTaskProgress";
    internal const string GoalLifecycleEscalateTaskFailure = "GoalLifecycle.EscalateTaskFailure";
    internal const string GoalLifecycleResetTaskForRetry = "GoalLifecycle.ResetTaskForRetry";
    internal const string GoalLifecycleHoldTaskForExistingHumanInput = "GoalLifecycle.HoldTaskForExistingHumanInput";
    internal const string GoalLifecycleSubmitHumanInputCoreSuppressionThreshold = "GoalLifecycle.SubmitHumanInputCore.SuppressionThreshold";
    internal const string GoalLifecycleRestoreTaskAfterHumanInputObsoletePlanner = "GoalLifecycle.RestoreTaskAfterHumanInput.ObsoletePlanner";
    internal const string GoalLifecycleRestoreTaskAfterHumanInput = "GoalLifecycle.RestoreTaskAfterHumanInput";
    internal const string GoalLifecycleDismissHumanInput = "GoalLifecycle.DismissHumanInput";
    internal const string RecordingRecordDispatchExecutionResultStaleDispatch = "Recording.RecordDispatchExecutionResult.StaleDispatch";
    internal const string RecordingRecordDispatchExecutionResultTerminalContractRepair = "Recording.RecordDispatchExecutionResult.TerminalContractRepair";
    internal const string RecordingRecordDispatchExecutionResultSubscriptionLimit = "Recording.RecordDispatchExecutionResult.SubscriptionLimit";
    internal const string RecordingRecordDispatchExecutionResultProviderConnectivity = "Recording.RecordDispatchExecutionResult.ProviderConnectivity";
    internal const string RecordingRecordDispatchExecutionResultDetachedWithoutWorkerResult = "Recording.RecordDispatchExecutionResult.DetachedWithoutWorkerResult";
    internal const string RecordingRecordDispatchExecutionResultProviderInterruption = "Recording.RecordDispatchExecutionResult.ProviderInterruption";
    internal const string RecordingRecordTaskDispatch = "Recording.RecordTaskDispatch";
    internal const string RecordingRecordTaskProcessCancelledConductor = "Recording.RecordTaskProcessCancelled.Conductor";
    internal const string RecordingRecordTaskProcessCancelled = "Recording.RecordTaskProcessCancelled";
    internal const string RecordingTryCompleteTaskWithPassingVerification = "Recording.TryCompleteTaskWithPassingVerification";
    internal const string DefaultTasksConcludeInterruptedDispatchRecovery = "DefaultTasks.ConcludeInterruptedDispatchRecovery";
    internal const string HumanInputRestoreRequeueAnsweredTesterBlocker = "HumanInputRestore.RequeueAnsweredTesterBlocker";
    internal const string UnstartedDispatchDiscardUnstartedDispatch = "UnstartedDispatch.DiscardUnstartedDispatch";
    internal const string UnstartedDispatchDiscardUnstartedPreparedDispatch = "UnstartedDispatch.DiscardUnstartedPreparedDispatch";
    internal const string UnchangedCandidateReinstatementReinstateUnchangedCandidateVerdict = "UnchangedCandidateReinstatement.ReinstateUnchangedCandidateVerdict";
    internal const string HumanInputSuppressionRestoreTaskAfterSuppressedAnsweredInputFailed = "HumanInputSuppression.RestoreTaskAfterSuppressedAnsweredInput.Failed";
    internal const string HumanInputSuppressionRestoreTaskAfterSuppressedAnsweredInputAssigned = "HumanInputSuppression.RestoreTaskAfterSuppressedAnsweredInput.Assigned";
    internal const string TaskProcessStartRecordingRecord = "TaskProcessStartRecording.Record";

    internal static void Record(IGoalLifecycleEventWriter writer, GoalId goalId, TaskStatusChange change, string site)
    {
        if (change.From != change.To)
            writer.AppendTaskStatusTransition(goalId, change.TaskId, change.From, change.To, site);
    }
}
