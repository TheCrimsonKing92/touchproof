using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

/// <summary>Repository-scoped adapter to the existing executor's merge and ref publication operations.</summary>
internal static class RepositoryIntegrationLanding
{
    internal static string Execute(Goal goal, RepositoryLandingTarget target, string goalBranch)
    {
        var temporary = Path.Combine(Path.GetTempPath(), "mcg-tuple-land-" + Guid.NewGuid().ToString("N"));
        try
        {
            if (!LandingExecutor.TryPrepareCandidateFromBoundMain(goal, target.Root, temporary, target.Tip,
                    goalBranch, out var candidate, out var error))
                throw new InvalidOperationException(error);
            Publish(target, candidate!);
            return candidate!;
        }
        finally { RemoveWorktree(target.Root, temporary); }
    }

    internal static void Publish(RepositoryLandingTarget target, string commit)
    {
        var inspection = RootCheckoutLandingGuard.Inspect(LandingExecutor.GitRunner,
            target.Root, target.Branch, target.Tip, commit);
        if (inspection.Outcome == RootCheckoutLandingGuard.Outcome.Hold)
            throw new InvalidOperationException(inspection.Reason);
        var result = LandingExecutor.UpdateRef(target.Root, target.Branch, commit, target.Tip);
        if (!result.Succeeded) throw new InvalidOperationException(result.Output + result.Error);
        if (inspection.Outcome == RootCheckoutLandingGuard.Outcome.FastForward
            && RootCheckoutLandingGuard.IsOnBranch(LandingExecutor.GitRunner, target.Root, target.Branch))
            Git(target.Root, "read-tree", "-m", "-u", target.Tip, commit);
    }

    internal static string Git(string root, params string[] arguments)
    {
        var result = LandingExecutor.GitRunner(root, arguments);
        if (!result.Succeeded || !result.ProcessStarted || result.DrainTimedOut)
            throw new InvalidOperationException($"git {arguments[0]}: {result.Output} {result.Error}");
        return result.Output.Trim();
    }

    internal static void RemoveWorktree(string root, string path)
    {
        // Removal is scoped to the unique path owned by this operation, never a repository-wide prune.
        _ = LandingExecutor.GitRunner(root, ["worktree", "remove", "--force", path]);
    }
}
