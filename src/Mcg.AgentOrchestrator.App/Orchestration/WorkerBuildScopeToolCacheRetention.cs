using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class WorkerBuildScopeToolCacheRetention
{
    private const int KeepNewest = 8;
    internal static readonly TimeSpan KeepAge = TimeSpan.FromDays(2);
    private const int AccessDenied = unchecked((int)0x80070005);
    private const int SharingViolation = unchecked((int)0x80070020);
    private const int LockViolation = unchecked((int)0x80070021);

    internal static void Sweep(string? cacheRoot, DateTimeOffset now, List<EvidenceRetentionDecision> decisions)
    {
        (string Path, DateTime LastWrite)[] entries;
        try
        {
            cacheRoot ??= WorkerBuildScopeToolCacheIdentity.Root();
            if ((File.GetAttributes(cacheRoot) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Scope tool cache root is a reparse point.");
            entries = new DirectoryInfo(cacheRoot).EnumerateDirectories()
                .Where(entry => entry.Name.Length == 64 && entry.Name.All(Uri.IsHexDigit) &&
                    (entry.Attributes & FileAttributes.ReparsePoint) == 0)
                .OrderByDescending(entry => entry.LastWriteTimeUtc)
                .ThenBy(entry => entry.Name, StringComparer.Ordinal)
                .Select(entry => (entry.FullName, entry.LastWriteTimeUtc))
                .ToArray();
        }
        catch (Exception error) when (error is DirectoryNotFoundException or FileNotFoundException) { return; }
        catch (Exception error) when (IsFilesystemFailure(error) ||
            error is ArgumentException or InvalidOperationException or NotSupportedException)
        {
            Record(decisions, cacheRoot ?? WorkerBuildScopeToolCacheIdentity.ClassPattern,
                EvidenceRetentionAction.RetainedUndecidable, "worker-build-scope-tool-cache-enumeration-failed", error: error);
            return;
        }

        var keepSince = now.UtcDateTime - KeepAge;
        foreach (var entry in entries.Skip(KeepNewest))
            if (entry.LastWrite < keepSince)
                Retire(cacheRoot, entry.Path, entry.LastWrite, decisions);
    }

    private static void Retire(string cacheRoot, string path, DateTime lastWrite, List<EvidenceRetentionDecision> decisions)
    {
        var retired = Path.Combine(cacheRoot, $".sweep-retired-{Guid.NewGuid():N}");
        long bytes = 0;
        try
        {
            long unreadable = 0;
            bytes = StorageHealthMeasurement.Measure(path, ref unreadable).Bytes;
            if (unreadable > 0)
                throw new IOException("Scope tool cache entry size could not be measured.");

            // A concurrent publisher may have replaced the entry since candidate selection.
            if ((File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != FileAttributes.Directory ||
                Directory.GetLastWriteTimeUtc(path) != lastWrite)
                return;
        }
        catch (Exception error) when (IsFilesystemFailure(error))
        {
            Record(decisions, path, EvidenceRetentionAction.RetainedUndecidable,
                "worker-build-scope-tool-cache-measurement-failed", error: error);
            return;
        }

        try
        {
            Directory.Move(path, retired);
        }
        catch (Exception error) when (IsFilesystemFailure(error))
        {
            var held = error.HResult is SharingViolation or LockViolation ||
                (OperatingSystem.IsWindows() && error.HResult == AccessDenied && HasDeleteSharingConflict(path));
            Record(decisions, path,
                held ? EvidenceRetentionAction.DeferredLocked : EvidenceRetentionAction.RetainedUndecidable,
                held ? "worker-build-scope-tool-cache-held" : "worker-build-scope-tool-cache-retirement-failed", bytes, error);
            return;
        }

        try
        {
            DeleteTree(retired);
            Record(decisions, path, EvidenceRetentionAction.Deleted, "worker-build-scope-tool-cache-evicted", bytes);
        }
        catch (Exception error) when (IsFilesystemFailure(error))
        {
            Record(decisions, retired, EvidenceRetentionAction.RetainedUndecidable,
                "worker-build-scope-tool-cache-reclaim-failed", bytes, error);
        }
    }

    private static void DeleteTree(string path)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.Directory) == 0)
        {
            File.Delete(path);
            return;
        }

        if ((attributes & FileAttributes.ReparsePoint) == 0)
            foreach (var child in Directory.EnumerateFileSystemEntries(path))
                DeleteTree(child);

        Directory.Delete(path);
    }

    // Windows can report access denied for a parent rename when a child denies delete sharing.
    // Request delete access without deleting anything to distinguish sharing from permissions.
    private static bool HasDeleteSharingConflict(string path)
    {
        const uint deleteAccess = 0x00010000;
        const uint openExisting = 3;
        const uint backupSemantics = 0x02000000;
        const uint openReparsePoint = 0x00200000;
        var pending = new Stack<string>();
        pending.Push(path);
        while (pending.TryPop(out var current))
        {
            using var handle = CreateFile(current, deleteAccess, FileShare.ReadWrite | FileShare.Delete,
                IntPtr.Zero, openExisting, backupSemantics | openReparsePoint, IntPtr.Zero);
            if (handle.IsInvalid && Marshal.GetLastPInvokeError() is 32 or 33)
                return true;

            try
            {
                if ((File.GetAttributes(current) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == FileAttributes.Directory)
                    foreach (var child in Directory.EnumerateFileSystemEntries(current))
                        pending.Push(child);
            }
            catch (Exception error) when (IsFilesystemFailure(error)) { }
        }

        return false;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string fileName, uint desiredAccess, FileShare shareMode,
        IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    private static bool IsFilesystemFailure(Exception error) =>
        error is IOException or UnauthorizedAccessException or System.Security.SecurityException;

    private static void Record(List<EvidenceRetentionDecision> decisions, string path, EvidenceRetentionAction action,
        string reason, long bytes = 0, Exception? error = null) =>
        decisions.Add(new(EvidenceArtifactFamily.WorkerBuildScopeToolCaches, action, path, null,
            EvidenceOwnerResolution.Unrecorded, reason, BytesAttempted: bytes,
            BytesReclaimed: action == EvidenceRetentionAction.Deleted ? bytes : 0, FailureExceptionType: error?.GetType().Name));
}
