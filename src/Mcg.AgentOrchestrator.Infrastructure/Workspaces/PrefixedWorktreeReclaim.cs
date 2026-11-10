namespace Mcg.AgentOrchestrator.Infrastructure;

// Only reserved, registered, detached worktrees enter this removal path. Goal retention is separate.
internal sealed class PrefixedWorktreeReclaim(string executionDirectory, GoalWorktreeCleanupHooks hooks)
{
    internal const string EligibleReason = "prefixed-reclaim-eligible";
    private int reclaimed, removalFailed, pruneFailed;
    private long reclaimedBytes;

    internal static string Classify(string directory, IReadOnlyList<GoalWorktreeRegistration>? records,
        DateTimeOffset referenceTime, Func<string, IReadOnlyList<WorktreeLockHolder>>? findHolders)
    {
        var name = Path.GetFileName(directory);
        if (Path.GetFileName(Path.GetDirectoryName(directory)) != GoalWorktrees.DirectoryName ||
            !(name.StartsWith("t-", StringComparison.Ordinal) || name.StartsWith("c-", StringComparison.Ordinal) ||
              name.StartsWith("p-", StringComparison.Ordinal)) || records is null || findHolders is null)
            return "registered";
        var record = records.SingleOrDefault(item =>
            GoalWorktreeLayout.NormalizePath(item.Path).Equals(GoalWorktreeLayout.NormalizePath(directory),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
        if (record is null || record.Locked || record.Bare || record.Branch is not null) return "registered";
        if (WorktreeTreeFreshness.Classify(directory, referenceTime, TimeSpan.FromDays(2), filesOnly: true) != "eligible")
            return "registered";
        return findHolders(directory).Count == 0 ? EligibleReason : "registered";
    }

    internal string Remove(string directory)
    {
        long bytes;
        try
        {
            bytes = MeasureBytes(new DirectoryInfo(directory));
            var result = hooks.RunWorktreeRemove(executionDirectory, GitCli.DefaultTimeoutMilliseconds, directory, true);
            if (!result.ProcessStarted || result.ExitCode != 0 || result.DrainTimedOut)
                throw new IOException($"git worktree remove failed: exit={result.ExitCode}; {result.Error}");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or
                                     System.ComponentModel.Win32Exception or TimeoutException)
        {
            removalFailed++;
            Warn(directory, "orphan-sweep-prefixed-remove", error);
            return "removal-failed";
        }

        reclaimed++;
        reclaimedBytes += bytes;
        try
        {
            var result = hooks.RunWorktreePrune(executionDirectory, GitCli.DefaultTimeoutMilliseconds, false);
            if (!result.ProcessStarted || result.ExitCode != 0 || result.DrainTimedOut)
                throw new IOException($"git worktree prune failed: exit={result.ExitCode}; {result.Error}");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or
                                     System.ComponentModel.Win32Exception or TimeoutException)
        {
            pruneFailed++;
            Warn(directory, "orphan-sweep-prefixed-prune", error);
        }
        return "removed";
    }

    internal void WriteSummary()
    {
        if (reclaimed + removalFailed == 0) return;
        Console.Error.WriteLine($"orphan-sweep-prefixed-reclaim reclaimed={reclaimed} reclaimedBytes={reclaimedBytes} removalFailed={removalFailed} pruneFailed={pruneFailed}");
    }

    private static long MeasureBytes(DirectoryInfo directory)
    {
        directory.Refresh();
        if (!directory.Exists || (directory.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Worktree metadata unavailable.");
        long bytes = 0;
        foreach (var child in directory.EnumerateFileSystemInfos())
        {
            if ((child.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Worktree contains a reparse point.");
            bytes += child is DirectoryInfo nested ? MeasureBytes(nested) : ((FileInfo)child).Length;
        }
        return bytes;
    }

    private void Warn(string path, string operation, Exception error)
    {
        var warning = new GoalWorktreeCleanupWarning(path, operation, error);
        try { hooks.CleanupWarningSink(warning); }
        catch { GoalWorktrees.DefaultCleanupWarningSink(warning); }
    }
}
