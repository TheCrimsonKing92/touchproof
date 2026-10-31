using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Declared-test identity prefixes are file paths; everything after the first :: is opaque.
internal static class DeclaredTestIdentityPaths
{
    internal static string PathPart(string identity)
    {
        var boundary = identity.IndexOf("::", StringComparison.Ordinal);
        return boundary < 0 ? identity : identity[..boundary];
    }

    internal static string? CandidateRoot(string? executionDirectory, GoalId goalId)
    {
        if (string.IsNullOrWhiteSpace(executionDirectory) || !Path.IsPathFullyQualified(executionDirectory))
            return null;
        try { return GoalWorktreeLayout.WorktreePath(executionDirectory, goalId); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    internal static string Relativize(string identity, string? worktreeRoot) =>
        Relativize(identity, worktreeRoot, OperatingSystem.IsWindows());

    internal static string Relativize(string identity, string? worktreeRoot, bool ignoreCase)
    {
        var path = PathPart(identity).Replace('\\', '/');
        if (string.IsNullOrWhiteSpace(worktreeRoot) || !Path.IsPathFullyQualified(path)) return identity;
        var root = worktreeRoot.Replace('\\', '/');
        if (!Path.IsPathFullyQualified(root)) return identity;
        try
        {
            // Lexical canonicalization works even after the candidate worktree is removed.
            root = Path.GetFullPath(root).Replace('\\', '/').TrimEnd('/') + "/";
            var fullPath = Path.GetFullPath(path).Replace('\\', '/');
            var comparison = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (!fullPath.StartsWith(root, comparison) || fullPath.Length == root.Length) return identity;
            return fullPath[root.Length..] + identity[PathPart(identity).Length..];
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return identity;
        }
    }

    internal static string? NormalizeRelative(string path)
    {
        path = path.Replace('\\', '/');
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path) || path.Contains(':') || path.Contains('\0'))
            return null;
        var segments = new List<string>();
        foreach (var segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".") continue;
            if (segment == "..")
            {
                if (segments.Count == 0) return null;
                segments.RemoveAt(segments.Count - 1);
            }
            else segments.Add(segment);
        }
        return segments.Count == 0 ? null : string.Join('/', segments);
    }
}
