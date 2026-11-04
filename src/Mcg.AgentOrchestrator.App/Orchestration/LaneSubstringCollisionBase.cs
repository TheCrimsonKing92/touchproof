using System.Text;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record LaneSubstringCollisionBase(string BaseManifestJson, string DiffText)
{
    internal static LaneSubstringCollisionBase? Read(string worktreePath, string integrationBranch)
    {
        try
        {
            var mergeBase = GitCli.Run(worktreePath, "merge-base", integrationBranch, "HEAD");
            if (!mergeBase.Succeeded || mergeBase.DrainTimedOut || !mergeBase.ProcessStarted) return null;
            var revision = mergeBase.Output.Trim();
            var manifest = GitCli.Run(worktreePath, "show", $"{revision}:config/acceptance-manifest.json");
            if (!manifest.Succeeded || manifest.DrainTimedOut || !manifest.ProcessStarted) return null;
            var diff = GitCli.Run(worktreePath, "diff", "-U0", "--no-color", "--no-ext-diff", "-M",
                revision, "--", "tests/");
            if (!diff.Succeeded || diff.DrainTimedOut || !diff.ProcessStarted) return null;
            var untracked = GitCli.Run(worktreePath, "ls-files", "--others", "--exclude-standard", "-z", "--", "tests/");
            if (!untracked.Succeeded || untracked.DrainTimedOut || !untracked.ProcessStarted) return null;
            var candidateDiff = new StringBuilder(diff.Output).AppendLine();
            // These files have no base side. Feed their added lines to the same
            // declaration comparison as tracked changes without mutating the index.
            foreach (var path in untracked.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries))
                foreach (var line in File.ReadLines(Path.Combine(worktreePath, path)))
                    candidateDiff.Append('+').AppendLine(line);
            return new LaneSubstringCollisionBase(manifest.Output, candidateDiff.ToString());
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or FormatException or ArgumentException)
        {
            return null;
        }
    }
}
