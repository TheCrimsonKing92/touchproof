namespace Mcg.AgentOrchestrator.Core;

public sealed partial class AgentOrchestratorKernel
{
    public TaskBrief BuildTaskBrief(
        GoalId goalId,
        TaskId taskId,
        string? modelFitTarget = null,
        string? workingDirectory = null,
        string? contextDirectory = null,
        string? targetBranchName = null,
        string? targetHeadCommit = null,
        IReadOnlyList<string>? reviewerScopeChangedFiles = null,
        string? reviewerScopeMergeBase = null,
        int? reviewerScopeTotalChangedFileCount = null,
        bool? reviewerMergeTreeClean = null,
        IReadOnlyList<string>? reviewerMergeTreeConflictPaths = null,
        int? reviewerMergeTreeTotalConflictPathCount = null,
        IReadOnlyList<ReviewFindingLocation>? reviewerRoundTouchedAnchors = null,
        string? reviewerRoundTouchProofDiagnostic = null,
        ReviewRetryCapReceipt? reviewRetryCap = null)
    {
        var source = BuildTaskBriefSource(
            goalId,
            taskId,
            modelFitTarget,
            workingDirectory,
            contextDirectory,
            targetBranchName,
            targetHeadCommit,
            reviewerScopeChangedFiles,
            reviewerScopeMergeBase,
            reviewerScopeTotalChangedFileCount,
            reviewerMergeTreeClean,
            reviewerMergeTreeConflictPaths,
            reviewerMergeTreeTotalConflictPathCount,
            reviewerRoundTouchedAnchors,
            reviewerRoundTouchProofDiagnostic,
            reviewRetryCap);
        return source.ProjectLegacyMarkedTextV1();
    }
}
