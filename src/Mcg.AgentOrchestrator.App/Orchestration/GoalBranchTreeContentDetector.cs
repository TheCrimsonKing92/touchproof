using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Proves equality only over the branch's changed paths, using immutable revisions.
internal static class GoalBranchTreeContentDetector
{
    internal static GoalBranchContentState Classify(
        string executionDirectory, string mainSha, string branch, string? branchTipSha,
        Func<string, IReadOnlyList<string>, GitCli.GitResult> gitRunner)
    {
        if (string.IsNullOrEmpty(branch) || !IsObjectId(mainSha) || !IsObjectId(branchTipSha))
            return GoalBranchContentState.Inconclusive;

        var mergeBase = gitRunner(executionDirectory, ["merge-base", "--all", mainSha, branchTipSha!]);
        if (!Succeeded(mergeBase)) return GoalBranchContentState.Inconclusive;
        var bases = mergeBase.Output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        if (bases.Length != 1 || !IsObjectId(bases[0])) return GoalBranchContentState.Inconclusive;

        var changed = ReadDiff(bases[0], branchTipSha!);
        if (changed is null || changed.Count == 0 || changed.Any(entry => !entry.IsBlobComparison))
            return GoalBranchContentState.Inconclusive;
        var paths = changed.Select(entry => entry.Path).ToHashSet(StringComparer.Ordinal);

        var differences = ReadDiff(mainSha, branchTipSha!);
        if (differences is null) return GoalBranchContentState.Inconclusive;
        var relevant = differences.Where(entry => paths.Contains(entry.Path)).ToArray();
        if (relevant.Any(entry => !entry.IsBlobComparison)) return GoalBranchContentState.Inconclusive;

        // Equal blob ids prove byte equality, including symlinks. Paths absent from
        // the diff are equal at both tips (also when absent at both); modes are ignored.
        return relevant.Any(entry => entry.OldMode == "000000" || entry.NewMode == "000000" ||
                                     entry.OldId != entry.NewId)
            ? GoalBranchContentState.AbsentFromMain
            : GoalBranchContentState.EquivalentToMain;

        List<Entry>? ReadDiff(string from, string to)
        {
            var result = gitRunner(executionDirectory,
                ["diff-tree", "-r", "-z", "--no-renames", "--no-abbrev", "--ignore-submodules=none", "--no-relative", from, to]);
            return Succeeded(result) ? ParseDiff(result.Output) : null;
        }
    }

    private static bool Succeeded(GitCli.GitResult result) =>
        result.ExitCode == 0 && result.ProcessStarted && !result.DrainTimedOut;

    private static bool IsObjectId(string? value) =>
        value is { Length: 40 or 64 } && value.All(Uri.IsHexDigit);

    private static List<Entry>? ParseDiff(string output)
    {
        if (output.Length == 0) return [];
        var fields = output.Split('\0');
        if (fields[^1].Length != 0 || fields.Length % 2 != 1) return null;
        var entries = new List<Entry>();
        var paths = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < fields.Length - 1; i += 2)
        {
            var header = fields[i].Split(' ');
            var path = fields[i + 1];
            if (header.Length != 5 || !header[0].StartsWith(':') || path.Length == 0 ||
                path.Contains('\ufffd') || !paths.Add(path)) return null;
            var oldMode = header[0][1..];
            var newMode = header[1];
            if (!ValidMode(oldMode) || !ValidMode(newMode) ||
                !IsObjectId(header[2]) || !IsObjectId(header[3]) || header[2].Length != header[3].Length ||
                (oldMode == "000000") != header[2].All(c => c == '0') ||
                (newMode == "000000") != header[3].All(c => c == '0')) return null;
            var validStatus = header[4] switch
            {
                "A" => oldMode == "000000" && newMode != "000000",
                "D" => oldMode != "000000" && newMode == "000000",
                "M" or "T" => oldMode != "000000" && newMode != "000000",
                _ => false
            };
            if (!validStatus) return null;
            entries.Add(new Entry(path, oldMode, newMode, header[2], header[3]));
        }
        return entries;
    }

    private static bool ValidMode(string mode) => mode is "000000" or "100644" or "100755" or "120000" or "160000";

    private sealed record Entry(string Path, string OldMode, string NewMode, string OldId, string NewId)
    {
        internal bool IsBlobComparison => OldMode != "160000" && NewMode != "160000";
    }
}
