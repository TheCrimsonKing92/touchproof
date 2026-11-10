namespace Mcg.AgentOrchestrator.Infrastructure;

// Build telemetry must never become an input to the tree it describes. External
// state retains custody; the common Git directory owns the repository fallback.
internal static class BuildReceiptStorePaths
{
    internal static string Resolve(string worktreePath, string stateDirectory)
    {
        var host = AcceptancePartitionVerdictCache.ResolveHostStateRoot(worktreePath);
        if (!IsWithin(stateDirectory, host) && !IsWithin(stateDirectory, worktreePath))
            return Path.Combine(stateDirectory, BuildReceiptLedger.FileName);

        var common = CommonDirectory(worktreePath);
        if (common is not null)
            return Path.Combine(common, ".orchestrator", BuildReceiptLedger.FileName);

        // Non-repository callers have no Git tree to attest (e.g. fake build hooks).
        return Path.Combine(stateDirectory, BuildReceiptLedger.FileName);
    }

    internal static IReadOnlyList<string> ReadPaths(string worktreePath, string stateDirectory)
    {
        var legacy = Path.Combine(AcceptancePartitionVerdictCache.ResolveHostStateRoot(worktreePath), ".orchestrator");
        var paths = new List<string>
        {
            Path.Combine(stateDirectory, BuildReceiptLedger.FileName),
            Path.Combine(legacy, BuildReceiptLedger.FileName)
        };
        // Include script/dispatch-host fallback receipts even when this process has
        // installed an external route, and retain historical rows without moving them.
        try
        {
            paths.Add(Resolve(worktreePath, legacy));
            paths.Add(BuildReceiptLedger.ResolveStorePath(worktreePath));
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { /* Historical/routed receipts remain readable when metadata is unavailable. */ }
        return paths.Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal).ToArray();
    }

    private static bool IsWithin(string path, string root)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(path));
        return !Path.IsPathRooted(relative) && relative != ".." &&
            !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    private static string? CommonDirectory(string worktreePath)
    {
        // This is custody, not a revision query. Local aliases, includes and Git
        // environment overrides must not redirect telemetry to a different repository.
        var entry = Path.Combine(Path.GetFullPath(worktreePath), ".git");
        string git;
        if (Directory.Exists(entry)) git = entry;
        else if (File.Exists(entry))
        {
            var pointer = File.ReadAllText(entry).Trim();
            if (!pointer.StartsWith("gitdir: ", StringComparison.Ordinal))
                throw new IOException("Invalid Git metadata pointer.");
            git = Path.GetFullPath(pointer[8..], Path.GetFullPath(worktreePath));
        }
        else return null;
        var commonFile = Path.Combine(git, "commondir");
        if (File.Exists(commonFile)) git = Path.GetFullPath(File.ReadAllText(commonFile).Trim(), git);
        if (!Directory.Exists(Path.Combine(git, "objects")) || !Directory.Exists(Path.Combine(git, "refs")))
            throw new IOException("Cannot resolve Git custody for the build receipt ledger.");
        return git;
    }
}
