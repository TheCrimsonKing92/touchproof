namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class AttemptDirectoryGoalOwner
{
    // The caller has already resolved the direct goal directory to exactly one known goal.
    internal static bool TryResolve(string storeRoot, string path, string resolvedGoalId, out string attemptSegment)
    {
        attemptSegment = string.Empty;
        var relative = Path.GetRelativePath(Path.GetFullPath(storeRoot), Path.GetFullPath(path));
        if (Path.IsPathRooted(relative)) return false;

        var segments = relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 3 || segments.Any(segment => segment is "." or "..") ||
            !segments[0].Equals(resolvedGoalId, StringComparison.OrdinalIgnoreCase))
            return false;

        attemptSegment = segments[1];
        return true;
    }
}
