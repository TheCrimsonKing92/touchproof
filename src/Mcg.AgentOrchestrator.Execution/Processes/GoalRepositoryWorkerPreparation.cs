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
}
