namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class WorktreeTreeFreshness
{
    internal static string Classify(string directory, DateTimeOffset referenceTime, TimeSpan minimumAge,
        bool filesOnly = false)
    {
        try
        {
            var pending = new Stack<FileSystemInfo>();
            pending.Push(new DirectoryInfo(directory));
            var cutoff = referenceTime.UtcDateTime - minimumAge;
            var foundFile = false;
            while (pending.TryPop(out var entry))
            {
                entry.Refresh();
                // Never follow a junction/symlink or authorize deletion from missing metadata.
                if (!entry.Exists || (entry.Attributes & FileAttributes.ReparsePoint) != 0) return "freshness-unknown";
                if ((!filesOnly || entry is FileInfo) && entry.LastWriteTimeUtc >= cutoff) return "recent";
                if (entry is FileInfo) foundFile = true;
                if (entry is DirectoryInfo childDirectory)
                    foreach (var child in childDirectory.EnumerateFileSystemInfos()) pending.Push(child);
            }
            return filesOnly && !foundFile ? "freshness-unknown" : "eligible";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return "freshness-unknown";
        }
    }
}
