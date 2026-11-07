using System.Collections.ObjectModel;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

/// <summary>Reads bindings for existing goal worktrees without creating or modifying resources.</summary>
public static class GoalRepositoryBindingResolver
{
    public static bool TryResolve(
        GoalRepositorySet repositories,
        GoalId goalId,
        IReadOnlyDictionary<string, string> checkoutDirectories,
        out IReadOnlyDictionary<string, (string WorktreePath, string IntegrationBranch)>? bindings,
        out string failureReason)
    {
        ArgumentNullException.ThrowIfNull(repositories);
        ArgumentNullException.ThrowIfNull(goalId);
        ArgumentNullException.ThrowIfNull(checkoutDirectories);
        bindings = null;
        failureReason = string.Empty;

        // Preserve canonical key equality regardless of the caller's dictionary comparer.
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

        var resolved = new Dictionary<string, (string WorktreePath, string IntegrationBranch)>(StringComparer.Ordinal);
        foreach (var key in repositories.Keys)
        {
            try
            {
                var checkout = snapshot[key];
                if (string.IsNullOrWhiteSpace(checkout) || !Directory.Exists(checkout))
                {
                    failureReason = key + ":missing-worktree";
                    return false;
                }
                checkout = Path.GetFullPath(checkout);
                var path = GoalRepositoryWorktreeLayout.WorktreePath(checkout, goalId, key);
                if (!Directory.Exists(path))
                {
                    failureReason = key + ":missing-worktree";
                    return false;
                }
                resolved.Add(key, (path, Read(checkout, "symbolic-ref", "--quiet", "--short", "HEAD")));
            }
            catch (Exception ex)
            {
                failureReason = key + ":" + ex.Message;
                return false;
            }
        }

        bindings = new ReadOnlyDictionary<string, (string WorktreePath, string IntegrationBranch)>(resolved);
        return true;
    }

    // Match the provisioner's branch lookup and git failure diagnostics.
    private static string Read(string checkout, params string[] args)
    {
        var result = GitCli.Run(checkout, args);
        if (!Succeeded(result)) throw new InvalidOperationException(Detail(result));
        return result.Output.TrimEnd('\r', '\n');
    }

    private static bool Succeeded(GitCli.GitResult result) =>
        result.Succeeded && !result.DrainTimedOut && result.ProcessStarted;

    private static string Detail(GitCli.GitResult result) =>
        string.IsNullOrWhiteSpace(result.Error)
            ? $"git-exit-{result.ExitCode}:drain-timed-out={result.DrainTimedOut}:started={result.ProcessStarted}"
            : result.Error.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)[0];
}
