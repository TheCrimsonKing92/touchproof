using System.Globalization;

namespace Mcg.AgentOrchestrator.Infrastructure;

public static partial class GoalWorktrees
{
    internal static StringComparer CommitFactKeyComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static readonly BoundedInsertionOrderMemo<string, bool> MergeCommitsOutsideBaseMemo =
        new(256, CommitFactKeyComparer);

    internal static bool IsFullObjectId(string? value) => value is { Length: 40 or 64 } &&
        value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    internal static string CommitFactKey(string directory, string first, string? second = null) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)) + '\0' + first + '\0' + second;

    // A squash may change patch history, but it must preserve exactly the merged candidate tree.
    private static bool TryComputeSquashEquivalence(
        string executionDirectory, string oldHeadSha, string newHeadSha, string integrationBranch, out string evidence)
    {
        evidence = string.Empty;
        if (string.IsNullOrWhiteSpace(executionDirectory) || !Directory.Exists(executionDirectory) ||
            !TryResolveCommit(executionDirectory, oldHeadSha, out var oldHead) ||
            !TryResolveCommit(executionDirectory, newHeadSha, out var newHead) ||
            !TryReadGit(executionDirectory, ["rev-list", "--parents", "-n", "1", newHead], out var parentLine))
            return false;

        var parents = parentLine.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parents.Length != 2 || !string.Equals(parents[0], newHead, StringComparison.OrdinalIgnoreCase) ||
            !CommitShaPattern.IsMatch(parents[1])) return false;
        var mainParent = parents[1];
        var onMain = GitCli.Run(executionDirectory, "merge-base", "--is-ancestor", mainParent, $"refs/heads/{integrationBranch}");
        if (!onMain.Succeeded || onMain.DrainTimedOut ||
            !HasMergeCommitsOutsideBase(executionDirectory, mainParent, oldHead) ||
            !TryReadCleanMergeTree(executionDirectory, mainParent, oldHead, out var mergedTree) ||
            !(GitRevisionReader.Shared.TryResolve(executionDirectory, $"{newHead}^{{tree}}", out var newTree) ||
              TryReadGit(executionDirectory, ["rev-parse", "--verify", $"{newHead}^{{tree}}"], out newTree)) ||
            !string.Equals(mergedTree, newTree, StringComparison.OrdinalIgnoreCase)) return false;

        evidence = $"squashed-merge-commits: {newHead} single parent {mainParent} on {integrationBranch}; " +
            $"tree {newTree} equals git merge-tree --write-tree {mainParent} {oldHead}";
        return true;
    }

    internal static bool HasMergeCommitsOutsideBase(string executionDirectory, string baseHead, string branchHead)
    {
        var key = IsFullObjectId(baseHead) && IsFullObjectId(branchHead)
            ? CommitFactKey(executionDirectory, baseHead, branchHead) : null;
        if (key is not null && MergeCommitsOutsideBaseMemo.TryGet(key, out var cached)) return cached;
        var result = GitCli.Run(executionDirectory, "rev-list", "--merges", "--count", $"{baseHead}..{branchHead}");
        if (!result.Succeeded || result.DrainTimedOut || !result.ProcessStarted ||
            !int.TryParse(result.Output.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var count))
            return false;
        var hasMerges = count > 0;
        if (key is not null) MergeCommitsOutsideBaseMemo.Store(key, hasMerges);
        return hasMerges;
    }

    private static bool TryReadCleanMergeTree(
        string executionDirectory, string baseHead, string branchHead, out string tree)
    {
        tree = string.Empty;
        try
        {
            var merge = new WorkerGitContext().ReadReviewerMergeTreeStatus(executionDirectory, baseHead, branchHead);
            if (!merge.IsClean || merge.TreeId is null || !CommitShaPattern.IsMatch(merge.TreeId)) return false;
            tree = merge.TreeId;
            return true;
        }
        catch (ReviewerMergeTreeStatusException)
        {
            return false;
        }
    }
}
