using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class GoalBranchCommitPreflight
{
    internal const string Trailer = "Operator-Commit: goal-branch-commit";

    internal static string Check(Goal goal, OrchestratorWorkspace workspace, bool resume,
        Func<GoalId, bool>? hasLiveAcceptanceAttempt)
    {
        var path = GoalWorktrees.TryResolve(workspace.ExecutionDirectory, goal.Id)
            ?? throw new InvalidOperationException("WorktreeMissing: the goal has no registered worktree.");
        var branch = Git(path, "branch", "--show-current");
        if (string.IsNullOrEmpty(branch))
            throw new InvalidOperationException("DetachedHead: worktree HEAD is detached.");
        if (!branch.Equals(GoalWorktrees.BranchName(goal.Id), StringComparison.Ordinal))
            throw new InvalidOperationException("BranchMismatch: worktree is not on the goal branch.");
        if (GoalLiveDispatchPredicate.HasLiveDispatch(goal))
            throw new InvalidOperationException("WorkerDispatchLive: goal has a running dispatch or pending completion build.");
        var live = hasLiveAcceptanceAttempt ?? GoalLiveAcceptancePredicate.ForWorkspace(workspace);
        // Any failure to read authoritative attempt state fails closed, before mutation.
        if (live(goal.Id))
            throw new InvalidOperationException("AcceptanceAttemptLive: goal has a live acceptance attempt.");
        if (HasPendingMerge(path))
            throw new InvalidOperationException("MergeInProgress: The goal worktree has an unfinished merge. Complete it with an ordinary reviewed git merge --continue, or use git merge --abort to drop it. goal-branch-commit only records one-parent commits.");
        var staged = HasDiff(path, "--cached");
        if (!resume && !staged)
            throw new InvalidOperationException("NothingStaged: the goal worktree index has no changes.");
        if (resume && staged)
            throw new InvalidOperationException("ResumeHasStagedChanges: resume requires an unchanged index.");
        if (HasDiff(path))
            throw new InvalidOperationException("UnstagedTrackedChanges: the goal worktree has unstaged tracked changes.");
        if (resume)
        {
            var trailer = Git(path, "log", "-1", "--format=%(trailers:key=Operator-Commit,valueonly)");
            if (!trailer.Split('\n', StringSplitOptions.TrimEntries).Contains("goal-branch-commit", StringComparer.Ordinal))
                throw new InvalidOperationException("ResumeNotApplicable: HEAD was not made by goal-branch-commit.");
            _ = Parent(path);
        }
        return path;
    }

    internal static string Parent(string path)
    {
        var parents = Git(path, "rev-list", "--parents", "-n", "1", "HEAD").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parents.Length != 2)
            throw new InvalidOperationException("ResumeNotApplicable: HEAD must have exactly one parent.");
        return parents[1];
    }

    internal static string Git(string path, params string[] args)
    {
        var result = GitCli.RunForRepository(path, args);
        if (!result.Succeeded)
            throw new InvalidOperationException($"GitFailure ({args[0]}): {result.Error.Trim()}");
        return result.Output.Trim();
    }

    private static bool HasPendingMerge(string path)
    {
        var result = GitCli.RunForRepository(path, "rev-parse", "-q", "--verify", "MERGE_HEAD");
        if (!result.ProcessStarted || result.DrainTimedOut)
            throw new InvalidOperationException($"GitFailure (rev-parse): {result.Error.Trim()}");
        return result.ExitCode switch
        {
            0 => true,
            1 when string.IsNullOrWhiteSpace(result.Error) => false,
            _ => throw new InvalidOperationException($"GitFailure (rev-parse): {result.Error.Trim()}")
        };
    }

    private static bool HasDiff(string path, params string[] flags)
    {
        var result = GitCli.RunForRepository(path, ["diff", .. flags, "--quiet", "--exit-code"]);
        return result.ExitCode switch
        {
            0 => false,
            1 => true,
            _ => throw new InvalidOperationException($"GitFailure (diff): {result.Error.Trim()}")
        };
    }
}
