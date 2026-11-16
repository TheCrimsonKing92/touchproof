namespace Mcg.AgentOrchestrator.Infrastructure;

// Infrastructure registers these optional readers once at module load.
// These readers provide positive evidence only. Missing or inconclusive readers preserve the git path.
internal static class WorktreeCleanTreeFastPath
{
    internal static Func<string, Func<string, string?>, string?>? CommonDirectoryLocator;
    internal static Func<string, Func<string, string?>, string?>? HeadTreeResolver;

    internal static string? TryLocateObjects(string root, Func<string, string?> environment)
    {
        try
        {
            var common = Volatile.Read(ref CommonDirectoryLocator)?.Invoke(root, environment);
            return string.IsNullOrEmpty(common) ? null : Path.Combine(common, "objects");
        }
        catch (Exception exception) when (exception is not OutOfMemoryException) { return null; }
    }

    internal static bool TryMatchClean(string root, Func<string, string?> environment,
        Func<string, Dictionary<string, string>?, string[], WorktreeTreeDigestGitStep> run,
        Func<string, Func<string, string?>, string?>? resolveHeadTree, out string treeId)
        => TryMatchClean(root, environment, run, resolveHeadTree, out treeId, out _);

    internal static bool TryMatchClean(string root, Func<string, string?> environment,
        Func<string, Dictionary<string, string>?, string[], WorktreeTreeDigestGitStep> run,
        Func<string, Func<string, string?>, string?>? resolveHeadTree, out string treeId, out string? dirtyStatus)
    {
        treeId = string.Empty;
        dirtyStatus = null;
        if (resolveHeadTree is null) return false;
        try
        {
            var status = run(root, new() { ["GIT_OPTIONAL_LOCKS"] = "0" },
                ["status", "--porcelain=v2", "-z", "--untracked-files=all", "--ignore-submodules=none"]);
            if (status.ExitCode != 0 || status.TimedOut) return false;
            if (status.Output.Length != 0)
            {
                dirtyStatus = status.Output;
                return false;
            }
            var resolved = resolveHeadTree(root, environment);
            if (resolved is null || (resolved.Length != 40 && resolved.Length != 64) || !resolved.All(Uri.IsHexDigit))
                return false;
            treeId = resolved;
            return true;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException) { return false; }
    }
}
