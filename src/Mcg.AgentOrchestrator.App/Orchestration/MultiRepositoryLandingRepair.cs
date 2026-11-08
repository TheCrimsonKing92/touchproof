using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record MultiRepositoryLandingRepairResult(bool Repaired, string Reason, string Detail);

/// <summary>Rebuilds compensated candidates; integration refs are never written here.</summary>
internal sealed class MultiRepositoryLandingRepair(OrchestratorWorkspace workspace, OrchestratorProjectRegistry registry)
{
    private const string WorktreeLeaf = "candidate";
    private readonly MultiRepositoryLandingRecordStore store = new(workspace.OrchestratorDirectory);
    private readonly MultiRepositoryAcceptanceReceiptStore receipts = new(workspace.OrchestratorDirectory);

    private sealed record Repository(string Key, string Root, string Branch, string Tip,
        IReadOnlyList<string> Compensations,
        RepositoryLandingRepairProgress? Progress);

    internal MultiRepositoryLandingRepairResult Run(Goal goal)
    {
        var failure = store.ReadFailure(goal.Id);
        if (failure is null) return Refuse("no-failure-record", goal.Id.Value);
        if (failure.ManualRepairKeys.Count > 0)
            return Refuse("manual-repair-required", string.Join(", ", failure.ManualRepairKeys));

        // Preflight the entire tuple before writing Git, receipts, or progress.
        var repositories = new List<Repository>();
        try
        {
            var attempt = store.ReadAttempt(goal.Id);
            if (attempt is not null && attempt.TupleCanonical != failure.TupleCanonical)
                return Refuse("repository-unavailable", "Failure and attempt tuples differ.");
            foreach (var key in failure.CompensatingCommits.Where(pair => pair.Value.Count > 0)
                         .Select(pair => pair.Key).Order(StringComparer.Ordinal))
            {
                if (goal.Repositories is null || !goal.Repositories.Keys.Contains(key, StringComparer.Ordinal))
                    return Refuse("repository-unavailable", key);
                var root = key == "default" ? workspace.ExecutionDirectory : goal.Repositories.CheckoutRoots[key];
                var branch = key == "default" ? GoalWorktrees.BranchName(goal.Id)
                    : GoalRepositoryWorktreeLayout.BranchName(goal.Id, key);
                var tip = Git(root, "rev-parse", "--verify", "refs/heads/" + branch);
                var integration = new MultiRepositoryIntegrationBases(workspace, registry).Resolve(key);
                if (branch == integration.Branch)
                    return Refuse("repository-unavailable", key + ": candidate is the integration branch.");
                foreach (var commit in failure.CompensatingCommits[key])
                {
                    Git(root, "cat-file", "-e", commit + "^{commit}");
                    if (!Ancestor(root, commit, integration.Tip))
                        return Refuse("repository-unavailable", key + ": compensation is outside integration history.");
                }
                var checkout = FindCheckout(root, branch);
                if (checkout is not null && !Clean(checkout)) return Refuse("workspace-dirty", key);
                var progress = attempt?.RepairProgress?.GetValueOrDefault(key);
                repositories.Add(new(key, root, branch, tip,
                    failure.CompensatingCommits[key], progress));
            }
        }
        catch (Exception ex) { return Refuse("repository-unavailable", ex.Message); }

        // Invalidation precedes every possible publication and survives interruption.
        receipts.MarkStale(goal.Id);
        foreach (var repository in repositories)
        {
            try
            {
                if (repository.Progress?.RebuiltTip is { } done && Ancestor(repository.Root, done, repository.Tip))
                {
                    Cleanup(repository.Root, repository.Progress.WorktreePath, goal.Id, repository.Key);
                    continue;
                }
                var progress = repository.Progress;
                // Unpublished work is disposable. If the candidate advanced after a conflict
                // or before publication, rebuild from its current tip, preserving that work.
                if (progress?.RebuiltTip is null || progress.StartingTip != repository.Tip)
                {
                    if (progress is not null) Cleanup(repository.Root, progress.WorktreePath, goal.Id, repository.Key);
                    // Git derives .git/worktrees/<name> from the leaf. Keep that name short:
                    // merge's stash child can reject a long explicit GIT_DIR on Windows.
                    var path = Path.Combine(OrchestratorTempRoot.GetParent(),
                        WorktreePrefix(goal.Id, repository.Key) + Guid.NewGuid().ToString("N"), WorktreeLeaf);
                    // Observe the local integration base when this repository actually starts,
                    // after any earlier repository has finished rebuilding.
                    var integrationTip = new MultiRepositoryIntegrationBases(workspace, registry).Resolve(repository.Key).Tip;
                    progress = new(repository.Tip, integrationTip, path);
                    store.RecordRepairProgress(goal.Id, failure.TupleCanonical, repository.Key, progress);
                    // All intermediate commits live off-branch. A conflict or process stop cannot
                    // leave half a rebuild on the candidate or require a destructive branch reset.
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    Git(repository.Root, "worktree", "add", "--detach", path, progress.StartingTip);
                    try
                    {
                        Git(path, "merge", "--no-edit", progress.IntegrationTip);
                        foreach (var commit in repository.Compensations.Reverse())
                            Git(path, "revert", "--no-edit", commit);
                        progress = progress with { RebuiltTip = Git(path, "rev-parse", "HEAD") };
                        // Save before publication, including the commit-to-progress crash window.
                        store.RecordRepairProgress(goal.Id, failure.TupleCanonical, repository.Key, progress);
                    }
                    catch
                    {
                        _ = LandingExecutor.GitRunner(path, ["merge", "--abort"]);
                        _ = LandingExecutor.GitRunner(path, ["revert", "--abort"]);
                        throw;
                    }
                }

                // Recheck immediately before advancing the candidate. No force, reset, or stash.
                var current = Git(repository.Root, "rev-parse", "refs/heads/" + repository.Branch);
                if (current != progress.StartingTip)
                    return Refuse("repair-conflict", repository.Key + ": candidate changed before publication.");
                var checkout = FindCheckout(repository.Root, repository.Branch);
                if (checkout is null)
                    Git(repository.Root, "update-ref", "refs/heads/" + repository.Branch, progress.RebuiltTip!, current);
                else
                {
                    if (!Clean(checkout)) return Refuse("workspace-dirty", repository.Key);
                    if (Git(checkout, "symbolic-ref", "HEAD") != "refs/heads/" + repository.Branch)
                        return Refuse("repair-conflict", repository.Key + ": checkout changed branches.");
                    Git(checkout, "merge", "--ff-only", progress.RebuiltTip!);
                }
                // Also record after publication; a stop before this write is covered by the
                // already durable prepared tip and the ancestry check on the next invocation.
                store.RecordRepairProgress(goal.Id, failure.TupleCanonical, repository.Key, progress);
                Cleanup(repository.Root, progress.WorktreePath, goal.Id, repository.Key);
            }
            catch (Exception ex)
            {
                var detail = repository.Key + ": " + ex.Message;
                try
                {
                    var saved = store.ReadAttempt(goal.Id)?.RepairProgress?.GetValueOrDefault(repository.Key);
                    if (saved is { RebuiltTip: null })
                        Cleanup(repository.Root, saved.WorktreePath, goal.Id, repository.Key);
                }
                catch (Exception cleanupError) { detail += " Cleanup failed: " + cleanupError.Message; }
                // Retain write-ahead state and all previously published candidates for retry.
                return Refuse("repair-conflict", detail);
            }
        }
        receipts.MarkStale(goal.Id);
        store.ClearFailureAndAttempt(goal.Id); // Last mutation: the failure hold now releases.
        return new(true, "repaired", "Candidates rebuilt; re-gate every declared repository.");
    }

    private static MultiRepositoryLandingRepairResult Refuse(string reason, string detail) => new(false, reason, detail);
    private static string Git(string root, params string[] args) => RepositoryIntegrationLanding.Git(root, args);

    private static bool Ancestor(string root, string ancestor, string tip)
    {
        var result = LandingExecutor.GitRunner(root, ["merge-base", "--is-ancestor", ancestor, tip]);
        if (!result.ProcessStarted || result.DrainTimedOut || result.ExitCode is not (0 or 1))
            throw new InvalidOperationException("Cannot inspect repair ancestry: " + result.Error);
        return result.Succeeded;
    }

    private static string? FindCheckout(string root, string branch)
    {
        var listing = Git(root, "worktree", "list", "--porcelain", "-z");
        var checkouts = listing.Split("\0\0", StringSplitOptions.RemoveEmptyEntries)
            .Select(record => record.Split('\0'))
            .Where(fields => fields.Contains("branch refs/heads/" + branch, StringComparer.Ordinal))
            .Select(fields => fields.Single(field => field.StartsWith("worktree ", StringComparison.Ordinal))[9..]).ToArray();
        if (checkouts.Length > 1) throw new InvalidOperationException("Candidate is checked out more than once.");
        return checkouts.SingleOrDefault();
    }

    private static bool Clean(string checkout)
    {
        if (Git(checkout, "status", "--porcelain", "--untracked-files=all").Length > 0) return false;
        foreach (var name in new[] { "MERGE_HEAD", "REVERT_HEAD", "CHERRY_PICK_HEAD", "sequencer", "rebase-merge", "rebase-apply" })
        {
            var path = Git(checkout, "rev-parse", "--path-format=absolute", "--git-path", name);
            if (File.Exists(path) || Directory.Exists(path)) return false;
        }
        return true;
    }

    private static string WorktreePrefix(GoalId id, string key) => "mcg-tuple-repair-" + id.Value + "-" + key + "-";

    private static void Cleanup(string root, string path, GoalId id, string key)
    {
        // A persisted path is a reference, not permission to remove arbitrary worktrees.
        var full = Path.GetFullPath(path);
        // Accept the previous flat layout too, so an interrupted repair survives an upgrade.
        var container = Path.GetFileName(full) == WorktreeLeaf ? Path.GetDirectoryName(full)! : full;
        if (!Path.GetFileName(container).StartsWith(WorktreePrefix(id, key), StringComparison.Ordinal) ||
            !string.Equals(Path.GetDirectoryName(container), Path.GetFullPath(OrchestratorTempRoot.GetParent()).TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Invalid repair worktree path.");
        if (Directory.Exists(full)) Git(root, "worktree", "remove", "--force", full);
        // Non-recursive removal refuses unexpected contents rather than discarding them.
        if (container != full && Directory.Exists(container)) Directory.Delete(container);
    }
}
