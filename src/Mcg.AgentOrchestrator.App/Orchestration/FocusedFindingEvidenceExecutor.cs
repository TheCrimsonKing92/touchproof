using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Owns execution and candidate consistency for a focused evidence target.
internal sealed class FocusedFindingEvidenceExecutor(OrchestratorWorkspace workspace, IGoalAcceptanceVerifier verifier)
{
    internal FocusedEvidenceRunResult Run(
        Goal goal, string request, DotnetBuildEnvironmentLease? stableSlotLease,
        bool runBaselineArm, CancellationToken cancellationToken,
        FindingEvidenceNegativeControl? negativeControl = null, IReadOnlyList<string>? revertPaths = null,
        FindingEvidenceMutation? mutation = null, SourceRevertedEvidenceContinuation? continuation = null,
        KeyedRepositoryEvidenceTarget? repositoryTarget = null)
    {
        var directory = workspace.ExecutionDirectory;
        var worktreePath = repositoryTarget?.WorktreePath ?? GoalWorktrees.TryResolve(directory, goal.Id);
        if (worktreePath is null)
            return new FocusedEvidenceRunResult(request, Accepted: false, Passed: false,
                Summary: "goal worktree not found for focused evidence request", Checks: []);

        if (repositoryTarget is not null && !string.Equals(repositoryTarget.CandidateHead,
                ConductorGitRevisionReader.ReadRequiredCommit(worktreePath, "HEAD"), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Repository candidate changed before focused evidence execution.");
        GoalOperationJournal.Begin(directory, goal, "conductor:finding-evidence", $"Running focused finding evidence: {request}");
        var result = AcceptanceExecutionRunner.RunFocusedVerification(
            verifier, worktreePath, goal.Id, request,
            stableSlotLease?.Environment.BuildPermitIndex, stableSlotLease,
            runBaselineArm, cancellationToken, repositoryTarget?.IntegrationBranch ?? workspace.IntegrationBranch,
            negativeControl, revertPaths, mutation,
            repositoryTarget is null ? NegativeControlRevertSetResolver.Resolve(goal) : null,
            repositoryTarget?.ProjectHomeDirectory ?? workspace.ProjectHomeDirectoryOrNull, continuation: continuation);
        if (repositoryTarget is not null && !string.Equals(repositoryTarget.CandidateHead,
                ConductorGitRevisionReader.ReadRequiredCommit(worktreePath, "HEAD"), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Repository candidate changed during focused evidence execution.");
        if (result.Passed)
            GoalOperationJournal.Completed(directory, goal, "conductor:finding-evidence", result.Summary);
        else
            GoalOperationJournal.Failed(directory, goal, "conductor:finding-evidence", result.Summary);
        return result;
    }
}
