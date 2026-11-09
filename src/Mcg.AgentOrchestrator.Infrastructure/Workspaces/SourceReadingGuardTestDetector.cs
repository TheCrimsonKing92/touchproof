namespace Mcg.AgentOrchestrator.Infrastructure;

/// <summary>Conservative textual evidence that a failing test reads repository source inputs.</summary>
internal static class SourceReadingGuardTestDetector
{
    private static readonly string[] Markers =
        ["VerifiedRepositoryRoot", "Directory.EnumerateFiles", "File.ReadAllText", "File.ReadAllLines"];

    internal static bool ReadsRepositorySources(
        string? sourceRoot,
        IReadOnlyList<string>? sourcePaths,
        AcceptanceTestSourceResolver.Request? request = null)
    {
        if (string.IsNullOrWhiteSpace(sourceRoot) || sourcePaths is not { Count: > 0 }) return false;

        var root = Path.GetFullPath(sourceRoot);
        foreach (var sourcePath in sourcePaths)
        {
            // The gate already resolved and parsed these files. Borrow its cache rather than reading again.
            var source = request is not null
                ? request.ReadSyntax(sourcePath)?.ToFullString()
                : ReadSource(root, sourcePath);
            if (source is not null && Markers.Any(marker => source.Contains(marker, StringComparison.Ordinal)))
                return true;
        }

        return false;
    }

    private static string? ReadSource(string root, string relativePath)
    {
        var normalized = relativePath.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
        if (Path.IsPathRooted(normalized)) return null;

        var path = Path.GetFullPath(Path.Combine(root, normalized));
        var prefix = Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !File.Exists(path)) return null;

        try
        {
            return File.ReadAllText(path);
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }
}
