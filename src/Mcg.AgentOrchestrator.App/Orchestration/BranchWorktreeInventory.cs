using System.Globalization;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed class BranchWorktreeInventory
{
    internal static readonly IReadOnlyList<string> BranchClasses = ["live", "merged", "unmerged"];
    internal static readonly IReadOnlyList<string> WorktreeClasses =
        ["goal", "impact_replay", "focused_evidence_baseline", "canary", "other"];

    internal IReadOnlyList<(string Name, string Class, string? Owner, long CommitsNotOnMain)> Branches { get; private init; } = [];
    internal IReadOnlyList<(string Path, string Class, bool DirectoryExists, string? GoalId, bool? GoalTerminal, string? Branch)> Worktrees { get; private init; } = [];

    internal static BranchWorktreeInventory Read(string repositoryDirectory, string integrationBranch,
        IReadOnlyCollection<Goal> goals,
        Func<string, IReadOnlyList<string>, GitCli.GitResult>? gitRunner = null)
    {
        gitRunner ??= (directory, args) => GitCli.RunForRepository(directory, args.ToArray());
        string Git(params string[] args)
        {
            var result = gitRunner(repositoryDirectory, args);
            if (!result.ProcessStarted || result.DrainTimedOut || result.ExitCode != 0)
                throw new InvalidOperationException($"git {args[0]} failed (exit {result.ExitCode}, started={result.ProcessStarted}, drain_timeout={result.DrainTimedOut}): {result.Error}");
            return result.Output;
        }

        var tips = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in Git("for-each-ref", "--format=%(refname:lstrip=2) %(objectname)", "refs/heads/")
                     .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split(' ');
            if (parts.Length != 2 || parts[0].Length == 0 ||
                parts[1].Length is not (40 or 64) || !parts[1].All(Uri.IsHexDigit))
                throw new InvalidOperationException("git for-each-ref returned a malformed local branch.");
            tips.Add(parts[0], parts[1]);
        }
        if (!tips.TryGetValue(integrationBranch, out var mainTip))
            throw new InvalidOperationException($"Cannot resolve refs/heads/{integrationBranch}; create or select the local integration branch before running branch-report.");

        // Capture object ids, rather than names that can move between independent reads.
        var mergedTips = Git("for-each-ref", "--format=%(objectname)", "--merged", mainTip, "refs/heads/")
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        var goalMap = goals.GroupBy(goal => GoalWorktreeLayout.BranchName(goal.Id), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.OrderBy(goal => goal.IsTerminal).First(), StringComparer.Ordinal);
        var worktrees = new List<(string Path, string Class, bool DirectoryExists, string? GoalId, bool? GoalTerminal, string? Branch)>();
        string? path = null;
        string? branch = null;
        void FinishWorktree()
        {
            if (path is null) return;
            var segments = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            var goalBranch = GoalBranch(branch);
            if (goalBranch is null && segments.Length >= 2 &&
                segments[^2].Equals(GoalWorktreeLayout.DirectoryName, StringComparison.OrdinalIgnoreCase) &&
                segments[^1].Length == 8 && segments[^1].All(Uri.IsHexDigit))
                goalBranch = "goal/" + segments[^1].ToLowerInvariant();
            var goal = goalBranch is null ? null : goalMap.GetValueOrDefault(goalBranch);
            var kind = goalBranch is not null ? "goal" :
                segments.Contains("impact-replay-worktrees", StringComparer.OrdinalIgnoreCase) ? "impact_replay" :
                segments.Contains(AcceptanceTempRootNames.FocusedEvidenceBaselinesRootDirectoryName, StringComparer.OrdinalIgnoreCase) ? "focused_evidence_baseline" :
                segments.Contains("post-landing-canary-worktrees", StringComparer.OrdinalIgnoreCase) ? "canary" : "other";
            worktrees.Add((path, kind, Directory.Exists(path),
                goal?.Id.Value ?? goalBranch?["goal/".Length..], goal?.IsTerminal, branch));
            path = null;
            branch = null;
        }
        // NUL fields preserve spaces, Unicode and embedded newlines without Git path quoting.
        foreach (var field in Git("worktree", "list", "--porcelain", "-z").Split('\0'))
        {
            if (field.Length == 0) FinishWorktree();
            else if (field.StartsWith("worktree ", StringComparison.Ordinal))
            {
                FinishWorktree();
                path = field["worktree ".Length..];
                if (path.Length == 0) throw new InvalidOperationException("git worktree list returned an empty path.");
            }
            else if (field.StartsWith("branch refs/heads/", StringComparison.Ordinal))
                branch = field["branch refs/heads/".Length..];
        }
        FinishWorktree();
        if (worktrees.Count == 0)
            throw new InvalidOperationException("git worktree list returned no registered worktrees.");

        var branches = new List<(string Name, string Class, string? Owner, long CommitsNotOnMain)>();
        foreach (var (name, tip) in tips.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            long count = 0;
            if (!mergedTips.Contains(tip) &&
                (!long.TryParse(Git("rev-list", "--count", $"{mainTip}..{tip}").Trim(),
                    NumberStyles.None, CultureInfo.InvariantCulture, out count) || count < 0))
                throw new InvalidOperationException($"git rev-list returned an invalid count for {name}.");
            var goalBranch = GoalBranch(name);
            var goal = goalBranch is null ? null : goalMap.GetValueOrDefault(goalBranch);
            var live = goal is { IsTerminal: false } ||
                (name == integrationBranch && worktrees.Any(tree => tree.Branch == name));
            var owner = goal?.Id.Value ?? (goalBranch is not null ? goalBranch["goal/".Length..] :
                name.Contains('/') ? name[..(name.IndexOf('/') + 1)] : null);
            branches.Add((name, live ? "live" : count == 0 ? "merged" : "unmerged", owner, count));
        }
        return new BranchWorktreeInventory
        {
            Branches = branches,
            Worktrees = worktrees.OrderBy(tree => tree.Path, StringComparer.Ordinal).ToArray()
        };
    }

    private static string? GoalBranch(string? branch)
    {
        if (branch is null || !branch.StartsWith("goal/", StringComparison.Ordinal) || branch.Length == "goal/".Length)
            return null;
        var suffix = branch.IndexOf("--repo-", StringComparison.Ordinal);
        return suffix < 0 ? branch : branch[..suffix];
    }
}
