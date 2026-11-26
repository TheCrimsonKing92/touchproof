using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Mcg.AgentOrchestrator.App.Cli;

internal readonly record struct ShadowFileIdentity(ulong Volume, ulong FileIdLow, ulong FileIdHigh);

internal static class ShadowStartReclaimer
{
    private const uint ReadAttributes = 0x80;
    private const uint DeleteAccess = 0x10000;
    private const uint OpenExisting = 3;
    private const uint OpenReparsePoint = 0x00200000;
    private const uint BackupSemantics = 0x02000000;
    private const int FileDispositionInfoEx = 21;
    private const uint FileDispositionOnClose = 8;

    internal static FileStream CreateRegisteredStore(string path, Action<ShadowFileIdentity> register)
    {
        // The OS removes the empty file if the supervisor dies before saving its identity.
        var handle = File.OpenHandle(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
            FileOptions.DeleteOnClose);
        try
        {
            register(ReadIdentity(handle));
            // Clear delete-on-close on the same handle only after the record is durable.
            if (!SetFileInformationByHandle(handle, FileDispositionInfoEx,
                    BitConverter.GetBytes(FileDispositionOnClose), sizeof(uint)))
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"Cannot keep registered store: {path}.");
            return new FileStream(handle, FileAccess.ReadWrite);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    internal static ShadowFileIdentity CaptureDirectory(string path)
    {
        using var handle = Open(path, directory: true) ?? throw new DirectoryNotFoundException(path);
        return ReadIdentity(handle);
    }

    internal static void DeleteStores(ShadowStartRun run, Func<string, ShadowFileIdentity?> readCandidateSidecar)
    {
        var directories = new Dictionary<string, SafeFileHandle>(StringComparer.OrdinalIgnoreCase);
        var files = new List<SafeFileHandle>();
        try
        {
            PinDirectory(directories, run.Scratch, run.ScratchIdentity);
            if (PinDirectory(directories, run.State, run.StateIdentity) is not null)
                OpenStores(run.State, readCandidateSidecar);
            var staging = PinDirectory(directories, run.Staging, run.StagingIdentity, delete: true);
            if (staging is not null)
                OpenStores(run.Staging);

            // Check every file before marking any for deletion. These exact, exclusively held
            // handles remain in force through deletion, including between failed and retried runs.
            foreach (var file in files)
                MarkForDeletion(file);
            foreach (var file in files)
                file.Dispose();

            if (staging is not null)
            {
                try { MarkForDeletion(staging); }
                catch (Win32Exception ex) when (ex.NativeErrorCode == 145) { /* Preserve unrelated entries. */ }
            }
        }
        finally
        {
            foreach (var file in files) file.Dispose();
            foreach (var directory in directories.Values.Reverse()) directory.Dispose();
        }

        void OpenStores(string root, Func<string, ShadowFileIdentity?>? readSidecar = null)
        {
            foreach (var name in run.Databases)
            {
                var database = Path.Combine(root, name);
                OpenStore(database, run.StoreIdentities);
                foreach (var suffix in ShadowStartCopyPlan.Suffixes.Skip(1))
                    OpenStore(database + suffix, run.CreatedSidecarIdentities, readSidecar);
            }
        }

        void OpenStore(string path, Dictionary<string, ShadowFileIdentity>? identities,
            Func<string, ShadowFileIdentity?>? readSidecar = null)
        {
            var handle = Open(path, directory: false, delete: true);
            if (handle is null) return;
            files.Add(handle);
            var actual = ReadIdentity(handle);
            if (!(identities?.TryGetValue(path, out var recorded) == true && actual == recorded) &&
                actual != readSidecar?.Invoke(path))
                throw new IOException($"Store file identity mismatch: {path}.");
        }
    }

    private static SafeFileHandle? PinDirectory(Dictionary<string, SafeFileHandle> directories, string path,
        ShadowFileIdentity? identity, bool delete = false)
    {
        if (Path.GetDirectoryName(path) is { } parent && !PinAncestors(directories, parent))
            return null;
        var pinned = Open(path, directory: true, delete);
        if (pinned is null) return null;
        directories.Add(path, pinned);
        if (identity is null || ReadIdentity(pinned) != identity)
            throw new IOException($"Directory identity mismatch: {path}.");
        return pinned;
    }

    private static bool PinAncestors(Dictionary<string, SafeFileHandle> directories, string path)
    {
        if (directories.ContainsKey(path)) return true;
        if (Path.GetDirectoryName(path) is { } parent && !PinAncestors(directories, parent)) return false;
        var handle = Open(path, directory: true);
        if (handle is null) return false;
        directories.Add(path, handle);
        return true;
    }

    private static SafeFileHandle? Open(string path, bool directory, bool delete = false)
    {
        var access = ReadAttributes | (delete ? DeleteAccess : 0);
        // GENERIC_READ ensures an existing opener must share read access, while share=0 excludes
        // every existing data handle and prevents reopening the file until it is gone.
        if (!directory) access |= 0x80000000;
        var share = directory ? FileShare.ReadWrite : FileShare.None;
        var handle = CreateFileW(path, access, (uint)share, 0, OpenExisting,
            OpenReparsePoint | (directory ? BackupSemantics : 0), 0);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            if (error is 2 or 3) return null;
            throw new Win32Exception(error, $"Cannot open owned path {path} for cleanup (Windows error {error}).");
        }
        try
        {
            if (!GetFileInformationByHandle(handle, out var info))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            var attributes = (FileAttributes)info.Attributes;
            if (attributes.HasFlag(FileAttributes.ReparsePoint) ||
                attributes.HasFlag(FileAttributes.Directory) != directory || (!directory && info.Links != 1))
                throw new IOException($"Unverifiable owned path: {path}.");
            return handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    internal static ShadowFileIdentity ReadIdentity(SafeFileHandle handle) =>
        GetFileInformationByHandleEx(handle, 18, out var identity, (uint)Marshal.SizeOf<ShadowFileIdentity>())
            ? identity
            : throw new Win32Exception(Marshal.GetLastWin32Error());

    private static void MarkForDeletion(SafeFileHandle handle)
    {
        if (!SetFileInformationByHandle(handle, 4, [1], 1)) // FileDispositionInfo
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        internal uint Attributes;
        internal System.Runtime.InteropServices.ComTypes.FILETIME Creation, Access, Write;
        internal uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint sharing, nint security,
        uint creation, uint flags, nint template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation info);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int kind,
        out ShadowFileIdentity identity, uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int kind, byte[] data, uint size);
}
