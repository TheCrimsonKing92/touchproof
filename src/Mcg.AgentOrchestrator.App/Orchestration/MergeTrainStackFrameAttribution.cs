using System.Globalization;
using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// A path is only a reference. Follow the reported train-tree line through history,
// and require all changes to that line since main to belong to one train member.
internal static class MergeTrainStackFrameAttribution
{
    internal static MergeTrainMemberBinding? TryAttribute(
        IReadOnlyList<string> resultPaths, string workspacePath,
        IReadOnlyList<MergeTrainMemberBinding> members, string? observedMainRevision,
        out IReadOnlyList<string> subjects, IReadOnlySet<string>? confirmedIdentities = null)
    {
        subjects = [];
        if (string.IsNullOrWhiteSpace(observedMainRevision) || members.Count == 0 ||
            members.Any(member => string.IsNullOrWhiteSpace(member.RebasedTrainHeadRevision))) return null;
        var failures = MergeTrainReachAttribution.ReadFailures(resultPaths, confirmedIdentities);
        if (failures is null) return null;
        try
        {
            var commits = new Dictionary<string, MergeTrainMemberBinding>(StringComparer.Ordinal);
            var previous = observedMainRevision;
            foreach (var member in members)
            {
                var range = GitCli.Run(workspacePath, "rev-list", previous + ".." + member.RebasedTrainHeadRevision);
                if (!range.Succeeded || range.DrainTimedOut || !range.ProcessStarted) return null;
                foreach (var commit in Lines(range.Output))
                    if (!commits.TryAdd(commit, member)) return null;
                previous = member.RebasedTrainHeadRevision!;
            }
            var frames = new Dictionary<string, MergeTrainMemberBinding?>(StringComparer.Ordinal);
            var evidence = new SortedSet<string>(StringComparer.Ordinal);
            MergeTrainMemberBinding? attributed = null;
            foreach (var failure in failures)
            {
                var frame = TopFrame(workspacePath, failure.StackTrace);
                if (frame is null) return null;
                var key = frame.Value.Path + ":" + frame.Value.Line.ToString(CultureInfo.InvariantCulture);
                if (!frames.TryGetValue(key, out var owner))
                {
                    var history = GitCli.Run(workspacePath, "log", "--format=train-commit:%H",
                        "-L" + frame.Value.Line.ToString(CultureInfo.InvariantCulture) + "," +
                        frame.Value.Line.ToString(CultureInfo.InvariantCulture) + ":" + frame.Value.Path,
                        observedMainRevision + "..HEAD");
                    if (!history.Succeeded || history.DrainTimedOut || !history.ProcessStarted) return null;
                    foreach (var line in Lines(history.Output).Where(line => line.StartsWith("train-commit:", StringComparison.Ordinal)))
                    {
                        if (!commits.TryGetValue(line[13..], out var touchedBy) ||
                            (owner is not null && owner.GoalId != touchedBy.GoalId)) return null;
                        owner = touchedBy;
                    }
                    frames.Add(key, owner);
                }
                if (owner is null || (attributed is not null && attributed.GoalId != owner.GoalId)) return null;
                attributed = owner;
                evidence.Add((failure.TestName ?? "") + "@" + key);
            }
            subjects = evidence.ToArray();
            return attributed;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or
            NotSupportedException or InvalidOperationException or System.ComponentModel.Win32Exception or TimeoutException)
        {
            return null;
        }
    }

    private static IEnumerable<string> Lines(string value) =>
        value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static (string Path, int Line)? TopFrame(string workspacePath, string? stack)
    {
        foreach (var line in Lines(stack ?? ""))
        {
            var match = Regex.Match(line, @" in (?<path>.+):line (?<n>\d+)\s*$",
                RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
            if (!match.Success) continue;
            var raw = match.Groups["path"].Value.Replace('\\', '/');
            var relative = Path.IsPathRooted(raw) ? Path.GetRelativePath(workspacePath, raw).Replace('\\', '/') : raw;
            var path = ExistingSource(workspacePath, relative);
            // Build artifacts can have another worktree root, or a deterministic /_/ root.
            if (path is null)
            {
                for (var index = 0; index < raw.Length; index++)
                {
                    if (index > 0 && raw[index - 1] != '/') continue;
                    path = ExistingSource(workspacePath, raw[index..]);
                    if (path is not null) break;
                }
            }
            if (path is null) continue;
            return int.TryParse(match.Groups["n"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number > 0
                ? (path, number) : null;
        }
        return null;
    }

    private static string? ExistingSource(string workspacePath, string path)
    {
        if (!(path.StartsWith("src/", StringComparison.OrdinalIgnoreCase) ||
              path.StartsWith("tests/", StringComparison.OrdinalIgnoreCase))) return null;
        var root = Path.GetFullPath(workspacePath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(workspacePath, path));
        return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) && File.Exists(full)
            ? Path.GetRelativePath(workspacePath, full).Replace('\\', '/') : null;
    }
}
