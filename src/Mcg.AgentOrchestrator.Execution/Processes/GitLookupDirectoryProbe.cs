namespace Mcg.AgentOrchestrator.Infrastructure;

// Owned by exactly one lookup, including its nested confirmation. Never retained by a reader.
internal sealed class GitLookupDirectoryProbe
{
    private readonly HashSet<string> ordinaryDirectories = new(StringComparer.Ordinal);

    // Counts attempted directory metadata queries; cached DirectoryInfo attributes need no second query.
    internal int Reads { get; private set; }

    internal bool HasOrdinaryDirectoryPath(string directory)
    {
        var walked = new List<string>();
        for (var current = new DirectoryInfo(directory); current is not null; current = current.Parent)
        {
            var key = Path.TrimEndingDirectorySeparator(current.FullName);
            if (ordinaryDirectories.Contains(key)) break;
            Reads++;
            if (!current.Exists || (current.Attributes & FileAttributes.ReparsePoint) != 0 ||
                current.Name != current.Name.TrimEnd(' ', '.') ||
                OperatingSystem.IsWindows() && current.Name.Contains('~')) return false;
            walked.Add(key);
        }
        // Remember only chains whose entire ancestry passed, including when joining a known chain.
        ordinaryDirectories.UnionWith(walked);
        return true;
    }

    internal static bool EntryExists(string path)
    {
        // Attributes preserves access/I/O failures while returning -1 for ordinary absence.
        var entry = new FileInfo(path);
        if (entry.Attributes != (FileAttributes)(-1)) return true;
        if (OperatingSystem.IsWindows())
        {
            // Windows also folds ERROR_NOT_READY into -1. Query the volume root with the
            // throwing API so an unavailable volume remains an I/O failure, not absence.
            try { _ = File.GetAttributes(Path.GetPathRoot(entry.FullName)!); }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
        return false;
    }
}
