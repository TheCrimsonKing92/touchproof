using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class ActivationFailedMainRevert
{
    internal enum Outcome { NotApplicable, Held, Reverted }

    internal sealed record Result(Outcome Status, string? RevertCommit = null, string? Reason = null);

    private const string RevertTrailer = "This reverts commit ";
    private const string LandingSubject = "Integrate goal/";

    internal static Result Revert(string? root, string? branch, string? failedCommit)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)
            || string.IsNullOrWhiteSpace(branch) || string.IsNullOrWhiteSpace(failedCommit)
            || failedCommit.Equals("none", StringComparison.OrdinalIgnoreCase))
            return new(Outcome.NotApplicable);

        try
        {
            var worktree = Run(root, "rev-parse", "--is-inside-work-tree");
            if (!worktree.Succeeded || worktree.Output.Trim() != "true")
                return new(Outcome.NotApplicable);

            var tip = ResolveCommit(root, $"refs/heads/{branch}");
            var failed = ResolveCommit(root, failedCommit);
            if (tip is null || failed is null)
                return new(Outcome.NotApplicable);

            var parents = RepositoryIntegrationLanding.Git(root, "rev-list", "--parents", "-1", failed).Split(' ');
            if (parents.Length != 3)
                return Held("failed commit is not a two-parent merge");

            var ancestry = Run(root, "merge-base", "--is-ancestor", failed, tip);
            if (ancestry.ExitCode == 1)
                return Held("failed merge is not reachable from the integration tip");
            if (!ancestry.Succeeded)
                return Held("could not check failed merge ancestry");

            var previous = FindPreviousRevert(root, failed, tip);
            if (previous is not null)
                return Held(previous);

            var result = PrepareRevert(root, failed, tip);
            if (result.RevertCommit is not { } revert)
                return result;

            var checkout = RootCheckoutLandingGuard.Inspect(LandingExecutor.GitRunner, root, branch, tip, revert);
            if (checkout.Outcome == RootCheckoutLandingGuard.Outcome.Hold)
                return Held(checkout.Reason!);

            // The duplicate check and the revert both used this tip. Only one attempt
            // can publish from it, even if another process prepared the same revert.
            var publish = LandingExecutor.UpdateRef(root, branch, revert, tip);
            if (!publish.Succeeded || !publish.ProcessStarted)
                return Held("could not update the integration branch from the prepared tip");

            // The successful ref update fixes the local outcome even if checkout sync fails.
            if (checkout.Outcome == RootCheckoutLandingGuard.Outcome.FastForward)
                AdoptedBuildPublicationReporting.Try(() =>
                {
                    if (RootCheckoutLandingGuard.IsOnBranch(LandingExecutor.GitRunner, root, branch))
                        RepositoryIntegrationLanding.Git(root, "read-tree", "-m", "-u", tip, revert);
                }, "activation-main-revert-checkout-sync");
            return result;
        }
        catch (Exception ex)
        {
            return Held($"git compensation failed ({ex.GetType().Name})");
        }
    }

    private static string? ResolveCommit(string root, string revision)
    {
        var result = Run(root, "rev-parse", "--verify", "--quiet", "--end-of-options", revision + "^{commit}");
        if (result.ExitCode == 1)
            return null;
        if (!result.Succeeded)
            throw new InvalidOperationException("Could not resolve a compensation revision.");
        return result.Output.Trim();
    }

    private static string? FindPreviousRevert(string root, string failed, string tip)
    {
        var subject = RepositoryIntegrationLanding.Git(root, "log", "-1", "--format=%s", failed, "--");
        var manualPrefix = subject.StartsWith(LandingSubject, StringComparison.Ordinal)
            ? $"Revert goal {subject[LandingSubject.Length..].Split(' ', 2)[0]} landing"
            : null;
        List<string> arguments =
        [
            "log", "--topo-order", "--reverse", "-z", "--format=%H%x00%s%x00%b",
            "--fixed-strings", $"--grep={RevertTrailer}"
        ];
        if (manualPrefix is not null)
            arguments.Add($"--grep={manualPrefix}");
        arguments.AddRange([$"{failed}..{tip}", "--"]);
        var fields = RepositoryIntegrationLanding.Git(root, arguments.ToArray()).Split('\0');

        // Each tracked revert flips the effect of the commit it names. Walk ancestors
        // first so reverting a revert (including a manual one) can follow that chain.
        var effects = new Dictionary<string, (string Original, int Direction)>(StringComparer.Ordinal);
        var history = new Dictionary<string, (int Balance, string? Reversal)>(StringComparer.Ordinal);
        for (var index = 0; index + 2 < fields.Length; index += 3)
        {
            var commit = fields[index];
            var target = RevertedCommit(fields[index + 2]);
            if (target == failed || (manualPrefix is not null && fields[index + 1].StartsWith(manualPrefix, StringComparison.Ordinal)))
            {
                effects.Add(commit, (commit, 1));
                history.Add(commit, (1, null));
            }
            else if (target is not null && effects.TryGetValue(target, out var effect))
            {
                var direction = -effect.Direction;
                effects.Add(commit, (effect.Original, direction));
                var state = history[effect.Original];
                history[effect.Original] = (state.Balance + direction, direction < 0 ? commit : state.Reversal);
            }
        }

        foreach (var (revert, state) in history)
            if (state.Balance <= 0)
                return $"earlier revert {revert} was itself reverted by {state.Reversal}; operator review required";
        return history.Count > 0 ? "failed merge was already reverted" : null;
    }

    private static string? RevertedCommit(string body)
    {
        foreach (var line in body.Split('\n'))
        {
            if (!line.StartsWith(RevertTrailer, StringComparison.Ordinal))
                continue;
            // Git uses a period for an ordinary revert and a comma for a merge revert.
            var target = line[RevertTrailer.Length..].TrimEnd();
            if (target.EndsWith(", reversing", StringComparison.Ordinal))
                return target[..^", reversing".Length];
            return target.TrimEnd('.');
        }
        return null;
    }

    private static Result PrepareRevert(string root, string failed, string tip)
    {
        var path = Path.Combine(OrchestratorTempRoot.GetParent(), "mcg-activation-revert-" + Guid.NewGuid().ToString("N"));
        var revertInProgress = false;
        try
        {
            RepositoryIntegrationLanding.Git(root, "worktree", "add", "--detach", path, tip);
            revertInProgress = true;
            var revert = Run(path, "revert", "--no-commit", "--no-reference", "-m", "1", failed);
            if (!revert.Succeeded)
                return Held("revert did not apply cleanly");

            var tipTree = RepositoryIntegrationLanding.Git(path, "rev-parse", tip + "^{tree}");
            var stagedTree = RepositoryIntegrationLanding.Git(path, "write-tree");
            if (stagedTree == tipTree)
                return Held("revert is empty because the change is already absent");

            // Reuse the message Git prepared, including its full-SHA revert marker.
            if (!Run(path, "commit", "--no-edit").Succeeded)
                return Held("could not commit the prepared revert");
            revertInProgress = false;

            var committed = RepositoryIntegrationLanding.Git(path, "log", "-1", "--format=%H%x00%T%x00%P%x00%b", "HEAD", "--")
                .Split('\0', 4);
            if (committed[1] == tipTree)
                return Held("revert is empty because the change is already absent");
            if (committed[2] != tip || RevertedCommit(committed[3]) != failed)
                return Held("prepared revert has an unexpected parent or missing revert marker");
            return new(Outcome.Reverted, committed[0]);
        }
        finally
        {
            try
            {
                if (revertInProgress)
                    _ = LandingExecutor.GitRunner(path, ["revert", "--abort"]);
            }
            finally
            {
                // Cleanup precedes publication so an unsuccessful cleanup cannot
                // turn an already-published branch update into a held outcome.
                var removal = Run(root, "worktree", "remove", "--force", path);
                if (!removal.Succeeded &&
                    (Directory.Exists(path) || RepositoryIntegrationLanding.Git(root, "worktree", "list", "--porcelain", "-z")
                        .Split('\0').Contains("worktree " + path.Replace('\\', '/'), StringComparer.OrdinalIgnoreCase)))
                    throw new InvalidOperationException("Could not remove the activation revert worktree.");
            }
        }
    }

    private static Result Held(string reason) => new(Outcome.Held, Reason: reason);

    private static GitCli.GitResult Run(string root, params string[] args)
    {
        var result = LandingExecutor.GitRunner(root, args);
        if (!result.ProcessStarted || result.DrainTimedOut)
            throw new InvalidOperationException($"Incomplete git {args[0]} result.");
        return result;
    }
}
