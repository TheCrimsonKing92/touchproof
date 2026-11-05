namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed record GoalWorktreeRegistration(string Path, string? Head, string? Branch, bool Locked, bool Bare);

// A failed listing is never a successful empty registration set.
internal sealed record GoalWorktreeRegistrationListing(bool Succeeded, IReadOnlySet<string> Paths, string? FailureReason)
{
    internal IReadOnlyList<GoalWorktreeRegistration> Records { get; init; } = [];
    internal bool Terminated { get; init; }
    internal static GitCli.GitResult RunGit(string executionDirectory) =>
        GitCli.Run(executionDirectory, "worktree", "list", "--porcelain", "-z");

    internal static GoalWorktreeRegistrationListing Read(string executionDirectory, GoalWorktreeCleanupHooks hooks)
    {
        try
        {
            var result = hooks.RunWorktreeList(executionDirectory);
            if (!result.ProcessStarted || result.ExitCode != 0 || result.DrainTimedOut)
                return Failed($"git worktree list exit={result.ExitCode} drain_timed_out={result.DrainTimedOut}: {result.Error}");

            var paths = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            var records = new List<GoalWorktreeRegistration>();
            // -z avoids git's quoted/escaped path representation, including embedded newlines.
            // Accept ordinary porcelain too so injected runners can use its documented text form.
            var nulDelimited = result.Output.Contains('\0');
            var fields = nulDelimited ? result.Output.Split('\0') : result.Output.Replace("\r\n", "\n").Split('\n');
            string? path = null;
            var hasHead = false;
            var hasBranch = false;
            var bare = false;
            string? headValue = null, branchValue = null;
            var locked = false;
            bool CompleteRecord() => path is null || (bare || (hasHead && hasBranch));
            foreach (var field in fields)
            {
                if (field.Length == 0)
                {
                    if (!CompleteRecord()) return Failed("Incomplete git worktree record.");
                    if (path is not null) records.Add(new(GoalWorktreeLayout.NormalizePath(path), headValue, branchValue, locked, bare));
                    path = null;
                    hasHead = hasBranch = bare = false;
                    headValue = branchValue = null;
                    locked = false;
                    continue;
                }
                if (field.StartsWith("worktree ", StringComparison.Ordinal))
                {
                    if (path is not null) return Failed("Missing git worktree record separator.");
                    path = field["worktree ".Length..];
                    if (!Path.IsPathFullyQualified(path) || path.StartsWith('"')) return Failed("Unusable git worktree path.");
                    paths.Add(GoalWorktreeLayout.NormalizePath(path));
                }
                else if (path is null) return Failed("Git worktree field without a path.");
                else if (field.StartsWith("HEAD ", StringComparison.Ordinal))
                {
                    if (hasHead) return Failed("Duplicate git worktree HEAD field.");
                    var head = field[5..];
                    if (head.Length is not (40 or 64) || !head.All(Uri.IsHexDigit)) return Failed("Unusable git worktree HEAD.");
                    hasHead = true;
                    headValue = head;
                }
                else if (field == "detached" || field.StartsWith("branch refs/", StringComparison.Ordinal))
                {
                    if (hasBranch) return Failed("Duplicate git worktree branch field.");
                    hasBranch = true;
                    branchValue = field == "detached" ? null : field[7..];
                }
                else if (field == "bare") bare = true;
                else if (field == "locked" || field.StartsWith("locked ", StringComparison.Ordinal)) locked = true;
                else if (field != "locked" && !field.StartsWith("locked ", StringComparison.Ordinal) &&
                         field != "prunable" && !field.StartsWith("prunable ", StringComparison.Ordinal))
                    return Failed("Unknown git worktree field.");
            }
            if (!CompleteRecord()) return Failed("Incomplete git worktree record.");
            if (path is not null) records.Add(new(GoalWorktreeLayout.NormalizePath(path), headValue, branchValue, locked, bare));
            return new(true, paths, null)
            {
                Records = records,
                Terminated = nulDelimited ? result.Output.EndsWith("\0\0", StringComparison.Ordinal) :
                    result.Output.Replace("\r\n", "\n").EndsWith("\n\n", StringComparison.Ordinal)
            };
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or
                                     System.ComponentModel.Win32Exception or ArgumentException or TimeoutException)
        {
            return Failed(error.Message);
        }
    }

    private static GoalWorktreeRegistrationListing Failed(string reason) => new(false, new HashSet<string>(), reason);
}
