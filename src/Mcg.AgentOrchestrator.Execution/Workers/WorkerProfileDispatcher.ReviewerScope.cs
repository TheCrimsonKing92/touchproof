using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

public static partial class WorkerProfileDispatcher
{
    private static void EnsureReviewerScopeForPreparation(
        TaskSpec task,
        string workingDirectory, string integrationBranch,
        ref IReadOnlyList<string>? preflightFindings,
        ref ReviewerChangedFileScope? reviewerScope,
        ref IReadOnlyList<string>? reviewerScopeChangedFiles,
        ref string? reviewerScopeMergeBase,
        ref int? reviewerScopeTotalChangedFileCount,
        ref bool? reviewerMergeTreeClean,
        ref IReadOnlyList<string>? reviewerMergeTreeConflictPaths,
        ref int? reviewerMergeTreeTotalConflictPathCount)
    {
        if (task.RequiredRole != AgentRole.Reviewer)
        {
            return;
        }

        if (reviewerScope is not null)
        {
            reviewerScopeChangedFiles = reviewerScope.ChangedFiles;
            reviewerScopeMergeBase = reviewerScope.MergeBase;
            reviewerScopeTotalChangedFileCount = reviewerScope.TotalChangedFileCount;
        }

        if (reviewerScopeChangedFiles is not null &&
            !string.IsNullOrWhiteSpace(reviewerScopeMergeBase) &&
            reviewerScopeTotalChangedFileCount.HasValue &&
            reviewerMergeTreeClean.HasValue &&
            reviewerMergeTreeConflictPaths is not null &&
            reviewerMergeTreeTotalConflictPathCount.HasValue)
        {
            return;
        }

        var findings = preflightFindings is null
            ? []
            : preflightFindings.ToList();
        var hasScope = reviewerScopeChangedFiles is not null &&
            !string.IsNullOrWhiteSpace(reviewerScopeMergeBase) &&
            reviewerScopeTotalChangedFileCount.HasValue;
        if (!hasScope)
        {
            var scope = AddReviewerChangedFileScopeFindings(findings, task, workingDirectory, integrationBranch);
            if (scope is null)
            {
                var errorCode = ResolvePreflightErrorCode(findings) ?? ReviewerScopeUnavailableErrorCode;
                var codePrefix = string.IsNullOrWhiteSpace(errorCode) ? string.Empty : $"{errorCode}: ";
                throw new WorkerSubscriptionPreflightException(
                    "Reviewer changed-file scope preflight failed: " + codePrefix + string.Join("; ", findings),
                    errorCode,
                    findings);
            }

            reviewerScope = scope;
            reviewerScopeChangedFiles = scope.ChangedFiles;
            reviewerScopeMergeBase = scope.MergeBase;
            reviewerScopeTotalChangedFileCount = scope.TotalChangedFileCount;
        }

        var hasMergeTree = reviewerMergeTreeClean.HasValue &&
            reviewerMergeTreeConflictPaths is not null &&
            reviewerMergeTreeTotalConflictPathCount.HasValue;
        if (hasMergeTree)
        {
            preflightFindings = findings;
            return;
        }

        var mergeTree = AddReviewerMergeTreeStatusFindings(findings, task, workingDirectory, integrationBranch, reviewerScope);
        if (mergeTree is null)
        {
            var errorCode = ResolvePreflightErrorCode(findings) ?? ReviewerMergeTreeUnavailableErrorCode;
            var codePrefix = string.IsNullOrWhiteSpace(errorCode) ? string.Empty : $"{errorCode}: ";
            throw new WorkerSubscriptionPreflightException(
                "Reviewer merge-tree status preflight failed: " + codePrefix + string.Join("; ", findings),
                errorCode,
                findings);
        }

        preflightFindings = findings;
        reviewerMergeTreeClean = mergeTree.IsClean;
        reviewerMergeTreeConflictPaths = mergeTree.ConflictPaths;
        reviewerMergeTreeTotalConflictPathCount = mergeTree.TotalConflictPathCount;
    }

    private static ReviewerChangedFileScope? AddReviewerChangedFileScopeFindings(
        List<string> findings,
        TaskSpec task,
        string workingDirectory, string integrationBranch)
    {
        if (task.RequiredRole != AgentRole.Reviewer)
        {
            findings.Add("reviewer-scope: changed-file contract not required for non-Reviewer role");
            return null;
        }

        try
        {
            var scope = new WorkerGitContext().ReadReviewerChangedFileScope(workingDirectory, integrationBranch);
            findings.Add(
                $"reviewer-scope: git diff --name-only {integrationBranch}...HEAD found {scope.TotalChangedFileCount} changed file(s) from merge-base {ShortSha(scope.MergeBase)}");
            return scope;
        }
        catch (ReviewerChangedFileScopeException ex)
        {
            findings.Add($"blocked: {ex.ErrorCode}: {ex.Message}");
            return null;
        }
    }

    private static string ShortSha(string value)
    {
        var trimmed = value.Trim();
        return trimmed.Length <= 12 ? trimmed : trimmed[..12];
    }

    private static ReviewerMergeTreeStatus? AddReviewerMergeTreeStatusFindings(
        List<string> findings,
        TaskSpec task,
        string workingDirectory, string integrationBranch, ReviewerChangedFileScope? scope)
    {
        if (task.RequiredRole != AgentRole.Reviewer)
        {
            findings.Add("reviewer-merge-tree: merge-tree contract not required for non-Reviewer role");
            return null;
        }

        try
        {
            var status = new WorkerGitContext().ReadReviewerMergeTreeStatus(workingDirectory,
                scope?.IntegrationCommit ?? integrationBranch, scope?.CandidateCommit ?? "HEAD");
            if (status.IsClean)
            {
                findings.Add($"reviewer-merge-tree: git merge-tree --write-tree --name-only {integrationBranch} HEAD is clean against current {integrationBranch}");
            }
            else
            {
                findings.Add(
                    $"reviewer-merge-tree: git merge-tree --write-tree --name-only {integrationBranch} HEAD found {status.TotalConflictPathCount} conflict path(s) against current {integrationBranch}");
                if (status.Truncated)
                {
                    findings.Add(
                        $"reviewer-merge-tree: conflict path prompt list truncated to {status.ConflictPaths.Count} path(s) from {status.TotalConflictPathCount}");
                }
            }

            return status;
        }
        catch (ReviewerMergeTreeStatusException ex)
        {
            findings.Add($"blocked: {ex.ErrorCode}: {ex.Message}");
            return null;
        }
    }
}
