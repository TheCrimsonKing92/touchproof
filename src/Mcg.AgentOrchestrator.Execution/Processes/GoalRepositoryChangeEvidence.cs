using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

// Read-only dispatch evidence from distinct declared goal worktrees, in declaration order.
internal sealed class GoalRepositoryChangeEvidence
{
    private readonly IReadOnlyList<Entry> _repositories;

    private GoalRepositoryChangeEvidence(IReadOnlyList<Entry> repositories) => _repositories = repositories;

    public int CommitsAfterDispatch => _repositories.Sum(entry => entry.Evidence.CommitsAfterDispatch);
    public bool HasRelevantCommitAfterDispatch => _repositories.Any(entry => entry.Evidence.HasRelevantCommitAfterDispatch);
    public string ChangedPathsSummary => Summarize(_repositories.Select(entry => entry.ChangedPathsSummary));

    public static GoalRepositoryChangeEvidence Collect(
        Goal goal, TaskSpec task, DateTimeOffset dispatchedAt, DispatchWorktreeCommitter committer)
    {
        var entries = new List<Entry>();
        var seen = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var target in GoalRepositoryWorkerPreparation.ResolveTargets(goal, task.RequiredRole))
        {
            var path = NormalizePath(target.WorktreePath);
            if (!seen.Add(path)) continue;

            try
            {
                GoalRepositoryWorkerPreparation.ValidateTarget(target, goal.Id, new SystemWorkerSandboxFileSystem());
                var inspection = committer.InspectGoalWorktree(target.WorktreePath, goal.Id, dispatchedAt,
                    forceRefresh: true, expectedBranch: GoalRepositoryWorktreeLayout.BranchName(goal.Id, target.Key));
                entries.Add(inspection.IsAvailable
                    ? new Entry(path, inspection.Evidence,
                        string.Join(" | ", inspection.Evidence.ChangedPaths.Select(changed => $"{target.Key}:{changed}")))
                    : new Entry(path, GoalWorktreeDispatchEvidence.Unknown,
                        $"{target.Key}:unavailable ({inspection.UnavailableReason}; {inspection.GitReceipt})"));
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or ArgumentException)
            {
                entries.Add(new Entry(path, GoalWorktreeDispatchEvidence.Unknown,
                    $"{target.Key}:unavailable ({DispatchWorktreeCommitter.NormalizeDiagnosticText(ex.Message)})"));
            }
        }
        return new GoalRepositoryChangeEvidence(entries);
    }

    public Summary CombineWithHome(string homeWorkingDirectory, GoalWorktreeDispatchEvidence home)
    {
        if (_repositories.Count == 0)
            return new Summary(home.CommitsAfterDispatch, home.HasRelevantCommitAfterDispatch, home.ChangedPathsSummary);

        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var homeIsDeclared = _repositories.Any(entry => entry.WorktreePath.Equals(NormalizePath(homeWorkingDirectory), comparison));
        return new Summary(
            CommitsAfterDispatch + (homeIsDeclared ? 0 : home.CommitsAfterDispatch),
            HasRelevantCommitAfterDispatch || (!homeIsDeclared && home.HasRelevantCommitAfterDispatch),
            Summarize((homeIsDeclared ? Enumerable.Empty<string>() : home.ChangedPaths)
                .Concat(_repositories.Select(entry => entry.ChangedPathsSummary))));
    }

    private static string NormalizePath(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static string Summarize(IEnumerable<string> parts)
    {
        var summary = string.Join(" | ", parts.Where(part => part.Length > 0));
        return summary.Length == 0 ? "none" : summary;
    }

    private sealed record Entry(string WorktreePath, GoalWorktreeDispatchEvidence Evidence, string ChangedPathsSummary);

    internal sealed record Summary(int CommitsAfterDispatch, bool HasRelevantCommitAfterDispatch, string ChangedPathsSummary);
}
