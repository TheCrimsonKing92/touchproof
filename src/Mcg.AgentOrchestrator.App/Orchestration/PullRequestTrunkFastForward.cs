using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Both merged delivery and open-PR refresh preserve the root checkout and local history.
internal static class PullRequestTrunkFastForward
{
    internal sealed record Result(string? LocalTip, string? Reconcile = null, string? Unavailable = null);

    internal static Result Advance(string root, string branch, string target)
    {
        GitCli.GitResult Git(params string[] args) => LandingExecutor.GitRunner(root, args);
        var local = Git("rev-parse", "--verify", "refs/heads/" + branch);
        if (!Ok(local)) return new(null, Unavailable: "could not resolve local integration tip");
        var tip = local.Output.Trim();
        var contains = Git("merge-base", "--is-ancestor", target, tip);
        if (Ok(contains)) return new(tip);
        if (!Negative(contains)) return new(tip, Unavailable: "could not establish local merge ancestry");
        var forward = Git("merge-base", "--is-ancestor", tip, target);
        if (!Ok(forward)) return Negative(forward)
            ? new(tip, Reconcile: "local integration branch diverges")
            : new(tip, Unavailable: "could not establish fast-forward ancestry");
        var guard = RootCheckoutLandingGuard.Inspect(LandingExecutor.GitRunner, root, branch, tip, target);
        if (guard.Outcome == RootCheckoutLandingGuard.Outcome.Hold) return new(tip, Reconcile: guard.Reason);
        if (!Ok(LandingExecutor.UpdateRef(root, branch, target, tip))) return new(tip, Reconcile: "local ref update failed");
        if (guard.Outcome == RootCheckoutLandingGuard.Outcome.FastForward &&
            RootCheckoutLandingGuard.IsOnBranch(LandingExecutor.GitRunner, root, branch) &&
            !Ok(Git("read-tree", "-m", "-u", tip, target)))
            return new(tip, Reconcile: "local checkout synchronization failed");
        return new(tip);
    }

    private static bool Ok(GitCli.GitResult result) => result.Succeeded && result.ProcessStarted && !result.DrainTimedOut;
    private static bool Negative(GitCli.GitResult result) => result.ExitCode == 1 && result.ProcessStarted && !result.DrainTimedOut;
}
