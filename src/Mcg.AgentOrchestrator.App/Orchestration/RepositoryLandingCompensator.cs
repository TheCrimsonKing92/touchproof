using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class RepositoryLandingCompensator
{
    internal static RepositoryLandingCompensationResult Revert(RepositoryLandingStep step)
    {
        if (step.LandedTip is null) throw new InvalidOperationException("Landing tip is unknown; manual repair required.");
        var current = RepositoryIntegrationLanding.Git(step.Root, "rev-parse", "refs/heads/" + step.Branch);
        if (LandingExecutor.GitRunner(step.Root, ["merge-base", "--is-ancestor", step.LandedTip, current]).ExitCode != 0)
            throw new InvalidOperationException("Landed tip is no longer reachable; manual repair required.");
        var commits = RepositoryIntegrationLanding.Git(step.Root, "rev-list", "--first-parent", step.PreTip + ".." + step.LandedTip)
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        var temporary = Path.Combine(Path.GetTempPath(), "mcg-tuple-revert-" + Guid.NewGuid().ToString("N"));
        var compensating = new List<string>();
        try
        {
            RepositoryIntegrationLanding.Git(step.Root, "worktree", "add", "--detach", temporary, current);
            foreach (var commit in commits)
            {
                var parents = RepositoryIntegrationLanding.Git(step.Root, "rev-list", "--parents", "-n", "1", commit).Split(' ');
                var arguments = parents.Length > 2
                    ? new[] { "revert", "--no-edit", "-m", "1", commit } : new[] { "revert", "--no-edit", commit };
                RepositoryIntegrationLanding.Git(temporary, arguments);
                compensating.Add(RepositoryIntegrationLanding.Git(temporary, "rev-parse", "HEAD"));
            }
            if (compensating.Count > 0)
                RepositoryIntegrationLanding.Publish(new(step.Key, step.Root, step.Branch, current), compensating[^1]);
            return new(compensating);
        }
        catch (Exception ex)
        {
            _ = LandingExecutor.GitRunner(temporary, ["revert", "--abort"]);
            // Publication can succeed before checkout synchronization throws. Keep the commit
            // evidence if it is now on the branch, while still requesting manual checkout repair.
            var published = false;
            if (compensating.Count > 0)
            {
                var head = LandingExecutor.GitRunner(step.Root, ["rev-parse", "refs/heads/" + step.Branch]);
                published = head.Succeeded && LandingExecutor.GitRunner(step.Root,
                    ["merge-base", "--is-ancestor", compensating[^1], head.Output.Trim()]).Succeeded;
            }
            return new(published ? compensating : [], ex.Message);
        }
        finally { RepositoryIntegrationLanding.RemoveWorktree(step.Root, temporary); }
    }

    internal static RepositoryLandingStep IdentifyMutation(RepositoryLandingStep step)
    {
        var current = RepositoryIntegrationLanding.Git(step.Root, "rev-parse", "refs/heads/" + step.Branch);
        if (current == step.PreTip) return step;
        if (LandingExecutor.GitRunner(step.Root, ["merge-base", "--is-ancestor", step.PreTip, current]).ExitCode != 0
            || LandingExecutor.GitRunner(step.Root, ["merge-base", "--is-ancestor", step.CandidateTip, step.PreTip]).ExitCode == 0)
            throw new InvalidOperationException("Cannot attribute the branch movement to this goal; manual repair required.");
        // Identify the executor's merge, not unrelated commits that may have arrived afterwards.
        var chain = RepositoryIntegrationLanding.Git(step.Root, "rev-list", "--first-parent", "--reverse", step.PreTip + ".." + current)
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        foreach (var commit in chain)
        {
            if (LandingExecutor.GitRunner(step.Root, ["merge-base", "--is-ancestor", step.CandidateTip, commit]).ExitCode == 0)
                return step with { LandedTip = commit };
        }
        throw new InvalidOperationException("Integration branch moved but the goal landing cannot be identified; manual repair required.");
    }
}
