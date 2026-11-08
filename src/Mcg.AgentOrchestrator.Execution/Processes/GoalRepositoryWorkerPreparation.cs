using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

public static class GoalRepositoryWorkerPreparation
{
    public sealed record Target(string Key, string CheckoutRoot, string WorktreePath);

    internal static IReadOnlyList<Target> ResolveTargets(Goal goal, AgentRole role)
    {
        if (!BackgroundDispatchRunner.IsSandboxWorktreeWritable(role) || goal.Repositories is not { } declaration)
            return [];

        return declaration.DeclaredOrder.Where(key => key != "default")
            .Select(key => new Target(key, declaration.CheckoutRoots[key],
                GoalRepositoryWorktreeLayout.WorktreePath(declaration.CheckoutRoots[key], goal.Id, key)))
            .ToArray();
    }

    internal static void ValidateTarget(Target target, GoalId goalId, IWorkerSandboxFileSystem fileSystem)
    {
        var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(target.WorktreePath));
        var checkout = Path.TrimEndingDirectorySeparator(Path.GetFullPath(target.CheckoutRoot));
        var expected = GoalRepositoryWorktreeLayout.WorktreePath(checkout, goalId, target.Key);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (target.Key == "default" || !path.Equals(expected, comparison) ||
            checkout.Equals(path, comparison) || checkout.StartsWith(path + Path.DirectorySeparatorChar, comparison) ||
            !fileSystem.DirectoryExists(path) ||
            !string.Equals(WorkerSandboxGitBoundary.EnsureWritableSandboxInLinkedWorktree(path, fileSystem),
                Path.Combine(path, ".git"), comparison))
            throw new InvalidOperationException($"Repository '{target.Key}' is not its provisioned goal worktree: '{path}'.");
    }

    internal static void Prepare(
        IReadOnlyList<Target> targets,
        GoalId goalId,
        string sandboxRoot,
        WorkerSandboxPreparer preparer,
        IWorkerIntegrityLabeler labeler,
        IWorkerSandboxFileSystem fileSystem)
    {
        // Refuse malformed targets before making any label changes.
        foreach (var target in targets) ValidateTarget(target, goalId, fileSystem);
        foreach (var target in targets)
        {
            var preparation = preparer.Prepare(target.WorktreePath, sandboxRoot);
            if (preparation.RecoveryAction is { } recovery)
                throw new InvalidOperationException(recovery.Reason);

            DispatchProcessHost.ProtectWorkspaceBoundary(target.WorktreePath, labeler);
            // The common git directory is outside this subtree and is never a labeler target.
            DispatchProcessHost.ProtectLinkedWorktreeGitFile(Path.Combine(target.WorktreePath, ".git"), labeler);
        }
    }

    internal static IReadOnlyList<string> Commit(
        Goal goal, TaskSpec task, bool shouldCommitDirtyWorktree,
        string standardOutput, string standardError, DispatchWorktreeCommitter committer)
    {
        var failures = new List<string>();
        foreach (var target in ResolveTargets(goal, task.RequiredRole))
        {
            try
            {
                ValidateTarget(target, goal.Id, new SystemWorkerSandboxFileSystem());
                var dispatch = task.LastDispatch ?? throw new InvalidOperationException("Repository commit requires a dispatch.");
                var branch = GoalRepositoryWorktreeLayout.BranchName(goal.Id, target.Key);
                var inspection = committer.InspectGoalWorktree(target.WorktreePath, goal.Id,
                    dispatch.DispatchedAt, forceRefresh: true, expectedBranch: branch);
                if (!inspection.IsAvailable)
                {
                    failures.Add($"repository={target.Key}; worktree inspection refused; " +
                        $"reason={inspection.UnavailableReason}; git_receipt={inspection.GitReceipt}.");
                    continue;
                }
                if (inspection.Evidence.IsClean) continue;

                var attempt = shouldCommitDirtyWorktree
                    ? committer.TryCommitWorktreeEdits(target.WorktreePath,
                        DispatchWorktreeCommitter.BuildOrchestratorCommitMessage(goal, task,
                            BackgroundDispatchRunner.BuildDispatchId(goal.Id, task.Id, dispatch), standardOutput,
                            standardError, inspection.Evidence.DirtyPaths), inspection.Evidence.DirtyPaths)
                    : CommitWorktreeEditsResult.Failed("Commit-on-behalf guards refused repository edits.");
                var afterCommit = attempt.Succeeded
                    ? committer.InspectGoalWorktree(target.WorktreePath, goal.Id,
                        dispatch.DispatchedAt, forceRefresh: true, expectedBranch: branch)
                    : inspection;
                if (!attempt.Succeeded || !afterCommit.IsAvailable || !afterCommit.Evidence.IsClean ||
                    !afterCommit.Evidence.HasRelevantCommitAfterDispatch)
                    failures.Add($"repository={target.Key}; " + DispatchWorktreeCommitter.BuildCommitOnBehalfFailureDiagnostic(
                        attempt.Succeeded ? "Repository worktree did not verify clean after commit." : attempt.Diagnostic,
                        afterCommit.Evidence));
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or ArgumentException)
            {
                failures.Add($"repository={target.Key}; commit-on-behalf refused: " +
                    DispatchWorktreeCommitter.NormalizeDiagnosticText(ex.Message));
            }
        }
        return failures;
    }
}
