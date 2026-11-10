namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class RelaunchBuildCacheRetention
{
    internal static void Sweep(string cacheParent, string repositoryRoot, DateTimeOffset now,
        List<EvidenceRetentionDecision> decisions)
    {
        if (!Directory.Exists(cacheParent)) return;
        string protectedKey;
        string[] candidates;
        try
        {
            protectedKey = RelaunchBuildCacheIdentity.RepositoryKey(repositoryRoot);
            if ((File.GetAttributes(cacheParent) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Cache parent is a reparse point.");
            candidates = Directory.GetDirectories(cacheParent);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or
                                     System.Security.SecurityException)
        {
            Record(cacheParent, EvidenceRetentionAction.RetainedUndecidable,
                "relaunch-build-cache-enumeration-failed", error: error);
            return;
        }

        foreach (var cache in candidates)
        {
            var key = Path.GetFileName(cache);
            if (key.Length != 16 || !key.All(Uri.IsHexDigit)) continue;
            // This skip precedes inspection, locking and deletion, regardless of age or lock state.
            if (key.Equals(protectedKey, StringComparison.OrdinalIgnoreCase))
            {
                Record(cache, EvidenceRetentionAction.Preserved, "relaunch-build-cache-current-repository");
                continue;
            }

            long bytes = 0;
            try
            {
                if (!IsStale(cache, now, out bytes)) continue;
                FileStream cacheLock;
                try
                {
                    cacheLock = File.Open(Path.Combine(cache, RelaunchBuildCacheIdentity.LockFileName),
                        FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                }
                catch (IOException error)
                {
                    Record(cache, EvidenceRetentionAction.Preserved, "relaunch-build-cache-locked", error: error);
                    continue;
                }

                using (cacheLock)
                {
                    // A producer may have refreshed the payload since the first inspection.
                    if (!IsStale(cache, now, out bytes)) continue;
                    foreach (var child in new DirectoryInfo(cache).EnumerateFileSystemInfos())
                    {
                        if (IsLockFile(child, cache)) continue;
                        if (child is DirectoryInfo directory) directory.Delete(recursive: true);
                        else child.Delete();
                    }
                }

                // Windows cannot unlink a FileShare.None handle. Only empty-shell cleanup follows
                // release; never recursively delete here, since a producer may acquire the lock.
                File.Delete(Path.Combine(cache, RelaunchBuildCacheIdentity.LockFileName));
                Directory.Delete(cache, recursive: false);
                Record(cache, EvidenceRetentionAction.Deleted, "relaunch-build-cache-evicted", bytes);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                Record(cache, EvidenceRetentionAction.RetainedUndecidable,
                    "relaunch-build-cache-reclaim-failed", error: error);
            }
        }

        void Record(string path, EvidenceRetentionAction action, string reason, long bytes = 0, Exception? error = null) =>
            decisions.Add(new(EvidenceArtifactFamily.RelaunchBuildCaches, action, path, null,
                EvidenceOwnerResolution.Unrecorded, reason, BytesAttempted: bytes,
                BytesReclaimed: action == EvidenceRetentionAction.Deleted ? bytes : 0,
                FailureExceptionType: error?.GetType().Name));
    }

    private static bool IsStale(string cache, DateTimeOffset now, out long bytes)
    {
        var pending = new Stack<FileSystemInfo>();
        pending.Push(new DirectoryInfo(cache));
        DateTime? newest = null;
        bytes = 0;
        while (pending.TryPop(out var entry))
        {
            entry.Refresh();
            if (!entry.Exists || (entry.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Cache freshness cannot be established.");
            if (entry is DirectoryInfo directory)
            {
                foreach (var child in directory.EnumerateFileSystemInfos()) pending.Push(child);
            }
            else if (!IsLockFile(entry, cache))
            {
                newest = newest is null || entry.LastWriteTimeUtc > newest.Value ? entry.LastWriteTimeUtc : newest;
                bytes += ((FileInfo)entry).Length;
            }
        }
        return newest is not null && newest.Value < now.UtcDateTime - TimeSpan.FromDays(2);
    }

    private static bool IsLockFile(FileSystemInfo entry, string cache) => entry is FileInfo &&
        entry.FullName.Equals(Path.Combine(cache, RelaunchBuildCacheIdentity.LockFileName),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
