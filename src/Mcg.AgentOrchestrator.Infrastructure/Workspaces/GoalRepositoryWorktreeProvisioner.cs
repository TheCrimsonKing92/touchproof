using System.Collections.ObjectModel;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

/// <summary>Provisions the declared repositories together, preserving resources from earlier calls.</summary>
public static class GoalRepositoryWorktreeProvisioner
{
    public static bool TryProvision(
        GoalRepositorySet repositories,
        GoalId goalId,
        IReadOnlyDictionary<string, string> checkoutDirectories,
        out IReadOnlyDictionary<string, (string WorktreePath, string IntegrationBranch)>? bindings,
        out string failureReason) =>
        TryProvision(repositories, goalId, checkoutDirectories,
            (checkout, path, branch, start) => GitCli.Run(checkout, "worktree", "add", "-b", branch, path, start),
            out bindings, out failureReason);

    internal static bool TryProvision(
        GoalRepositorySet repositories,
        GoalId goalId,
        IReadOnlyDictionary<string, string> checkoutDirectories,
        Func<string, string, string, string, GitCli.GitResult> addWorktree,
        out IReadOnlyDictionary<string, (string WorktreePath, string IntegrationBranch)>? bindings,
        out string failureReason)
    {
        ArgumentNullException.ThrowIfNull(repositories);
        ArgumentNullException.ThrowIfNull(goalId);
        ArgumentNullException.ThrowIfNull(checkoutDirectories);
        ArgumentNullException.ThrowIfNull(addWorktree);
        bindings = null;
        failureReason = string.Empty;

        var snapshot = new Dictionary<string, string>(checkoutDirectories, StringComparer.Ordinal);
        foreach (var key in repositories.Keys)
        {
            if (snapshot.ContainsKey(key)) continue;
            failureReason = key + ":missing-binding";
            return false;
        }
        foreach (var key in snapshot.Keys.OrderBy(key => key, StringComparer.Ordinal))
        {
            if (repositories.Keys.Contains(key, StringComparer.Ordinal)) continue;
            failureReason = key + ":unexpected-binding";
            return false;
        }

        var targets = new List<(string Key, string Checkout, string Path, string Branch, string IntegrationBranch, bool Reuse)>();
        foreach (var key in repositories.Keys)
        {
            try
            {
                var checkout = snapshot[key];
                if (string.IsNullOrWhiteSpace(checkout) || !Directory.Exists(checkout))
                    throw new InvalidOperationException("checkout-missing");
                checkout = Path.GetFullPath(checkout);
                var integrationBranch = Read(checkout, "symbolic-ref", "--quiet", "--short", "HEAD");
                Read(checkout, "rev-parse", "--verify", "--quiet", "HEAD^{commit}");
                var path = GoalRepositoryWorktreeLayout.WorktreePath(checkout, goalId, key);
                var branch = GoalRepositoryWorktreeLayout.BranchName(goalId, key);
                var registered = RegisteredBranch(checkout, path);
                var reuse = registered is not null;
                if (reuse)
                {
                    if (!File.Exists(System.IO.Path.Combine(path, ".git"))
                        || !SamePath(Read(checkout, "rev-parse", "--path-format=absolute", "--git-common-dir"),
                            Read(path, "rev-parse", "--path-format=absolute", "--git-common-dir")))
                        throw new InvalidOperationException("layout-conflict:foreign-or-missing-worktree");
                    if (registered != "refs/heads/" + branch
                        || Read(path, "symbolic-ref", "--quiet", "--short", "HEAD") != branch)
                        throw new InvalidOperationException("layout-conflict:wrong-branch");
                }
                else if (Path.Exists(path) || BranchExists(checkout, branch))
                    throw new InvalidOperationException("layout-conflict:path-or-branch-exists");

                if (targets.Any(target => SamePath(target.Path, path)))
                    throw new InvalidOperationException("layout-conflict:duplicate-destination");
                targets.Add((key, checkout, path, branch, integrationBranch, reuse));
            }
            catch (Exception ex)
            {
                failureReason = key + ":" + ex.Message;
                return false;
            }
        }

        // Only absent resources enter this list, before add, so even a partially failed add is owned.
        // Calls assume exclusive access to these goal resources; no ambient or cross-process lock is added.
        var created = new List<(string Checkout, string Path, string Branch)>();
        foreach (var target in targets)
        {
            if (target.Reuse) continue;
            try
            {
                if (Path.Exists(target.Path) || BranchExists(target.Checkout, target.Branch)
                    || RegisteredBranch(target.Checkout, target.Path) is not null)
                    throw new InvalidOperationException("layout-conflict:resources-changed-before-add");
                created.Add((target.Checkout, target.Path, target.Branch));
                var result = addWorktree(target.Checkout, target.Path, target.Branch, target.IntegrationBranch);
                if (!Succeeded(result)) throw new InvalidOperationException(Detail(result));
            }
            catch (Exception ex)
            {
                failureReason = target.Key + ":worktree-add-failed:" + ex.Message + Rollback(created);
                return false;
            }
        }

        var complete = targets.ToDictionary(target => target.Key,
            target => (WorktreePath: target.Path, IntegrationBranch: target.IntegrationBranch), StringComparer.Ordinal);
        bindings = new ReadOnlyDictionary<string, (string WorktreePath, string IntegrationBranch)>(complete);
        return true;
    }

    private static string Rollback(List<(string Checkout, string Path, string Branch)> created)
    {
        var residuals = new List<string>();
        foreach (var target in created.AsEnumerable().Reverse())
        {
            var errors = new List<string>();
            try
            {
                if (Path.Exists(target.Path) || RegisteredBranch(target.Checkout, target.Path) is not null)
                {
                    var remove = GitCli.Run(target.Checkout, "worktree", "remove", "--force", target.Path);
                    if (!Succeeded(remove))
                    {
                        errors.Add(Detail(remove));
                        // The exact layout destination was absent before add. Do not prune other registrations.
                        if (Directory.Exists(target.Path)) Directory.Delete(target.Path, recursive: true);
                        else if (File.Exists(target.Path)) File.Delete(target.Path);
                        if (RegisteredBranch(target.Checkout, target.Path) is not null)
                        {
                            var unregister = GitCli.Run(target.Checkout, "worktree", "remove", "--force", target.Path);
                            if (!Succeeded(unregister)) errors.Add(Detail(unregister));
                        }
                    }
                }
            }
            catch (Exception ex) { errors.Add(ex.Message); }
            try
            {
                if (Path.Exists(target.Path) || RegisteredBranch(target.Checkout, target.Path) is not null)
                    residuals.Add(";cleanup-residual:" + target.Path + ":" + CleanupDetail(errors));
            }
            catch (Exception ex)
            {
                residuals.Add(";cleanup-residual:" + target.Path + ":" + string.Join(" | ", errors.Append(ex.Message)));
            }

            // Always attempt branch cleanup even when removing its worktree failed.
            errors.Clear();
            try
            {
                if (BranchExists(target.Checkout, target.Branch))
                {
                    var remove = GitCli.Run(target.Checkout, "branch", "-D", target.Branch);
                    if (!Succeeded(remove)) errors.Add(Detail(remove));
                }
                if (BranchExists(target.Checkout, target.Branch))
                    residuals.Add(";cleanup-residual:" + target.Branch + ":" + CleanupDetail(errors));
            }
            catch (Exception ex)
            {
                residuals.Add(";cleanup-residual:" + target.Branch + ":" + string.Join(" | ", errors.Append(ex.Message)));
            }
        }
        return string.Concat(residuals);
    }

    private static string? RegisteredBranch(string checkout, string path)
    {
        // NUL-delimited porcelain avoids Git's quoted-path format, including spaces and Unicode.
        var fields = Read(checkout, "worktree", "list", "--porcelain", "-z").Split('\0');
        var matches = false;
        foreach (var field in fields)
        {
            if (field.StartsWith("worktree ", StringComparison.Ordinal))
                matches = SamePath(field[9..], path);
            else if (matches && field.StartsWith("branch ", StringComparison.Ordinal))
                return field[7..];
            else if (matches && field == "detached")
                return string.Empty;
        }
        return null;
    }

    private static bool BranchExists(string checkout, string branch)
    {
        var result = GitCli.Run(checkout, "show-ref", "--verify", "--quiet", "refs/heads/" + branch);
        if (Succeeded(result)) return true;
        if (result.ExitCode == 1 && !result.DrainTimedOut && result.ProcessStarted) return false;
        throw new InvalidOperationException(Detail(result));
    }

    private static bool SamePath(string left, string right) =>
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static string Read(string checkout, params string[] args)
    {
        var result = GitCli.Run(checkout, args);
        if (!Succeeded(result)) throw new InvalidOperationException(Detail(result));
        return result.Output.TrimEnd('\r', '\n');
    }

    private static bool Succeeded(GitCli.GitResult result) =>
        result.Succeeded && !result.DrainTimedOut && result.ProcessStarted;

    private static string CleanupDetail(List<string> errors) =>
        errors.Count == 0 ? "resource-still-present-after-cleanup" : string.Join(" | ", errors);

    private static string Detail(GitCli.GitResult result) =>
        string.IsNullOrWhiteSpace(result.Error)
            ? $"git-exit-{result.ExitCode}:drain-timed-out={result.DrainTimedOut}:started={result.ProcessStarted}"
            : result.Error.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)[0];
}
