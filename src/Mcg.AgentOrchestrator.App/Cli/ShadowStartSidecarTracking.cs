using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.Win32.SafeHandles;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class ShadowStartSidecarTracking
{
    internal const string RecordPathVariable = "MCG_SHADOW_START_RUN_RECORD";

    private const uint CreateNew = 1;
    private const uint OpenAlways = 4;
    private static CreateFileCallback? _createFile;

    internal static void InstallForCandidate(string state)
    {
        if (!OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable(RecordPathVariable) is not { } record)
            return;
        Environment.SetEnvironmentVariable(RecordPathVariable, null);

        using var process = Process.GetCurrentProcess();
        var creator = SpawnProcessIdentityReader.ReadForRegistration(process).Identity
            ?? throw new IOException("Shadow-start candidate identity is unavailable.");
        var run = ShadowStartRunRegistry.ReadCandidateRun(record, state, creator);
        var paths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in run.Databases)
        {
            var database = Path.Combine(run.State, name);
            if (run.StoreIdentities?.ContainsKey(database) != true) continue;
            foreach (var suffix in ShadowStartCopyPlan.Suffixes.Skip(1))
                paths.Add(database + suffix, database + suffix);
        }
        if (paths.Count == 0) return;

        RuntimeHelpers.RunClassConstructor(typeof(SqliteConnection).TypeHandle);
        if (SQLitePCL.raw.GetNativeLibraryName() != "e_sqlite3")
            throw new InvalidOperationException("Shadow-start candidate sidecar tracking requires the bundled SQLite VFS.");

        var pointer = FindVfs("win32");
        var vfs = pointer == IntPtr.Zero ? default : Marshal.PtrToStructure<SqliteVfs>(pointer);
        if (vfs.Version < 3 || vfs.SetSystemCall == IntPtr.Zero)
            throw new InvalidOperationException("Shadow-start candidate SQLite VFS cannot report sidecar creation.");

        // SQLite's Windows VFS shares its syscall table across its built-in variants. Install
        // before opening stores in this dedicated candidate process and root the callback for its lifetime.
        _createFile = OpenAndRecordCreation;
        var setSystemCall = Marshal.GetDelegateForFunctionPointer<SetSystemCallCallback>(vfs.SetSystemCall);
        if (setSystemCall(pointer, "CreateFileW", Marshal.GetFunctionPointerForDelegate(_createFile)) != 0)
            throw new InvalidOperationException("Shadow-start candidate SQLite VFS rejected sidecar tracking.");

        nint OpenAndRecordCreation(nint filename, uint access, uint sharing, nint security, uint disposition,
            uint flags, nint template)
        {
            var handle = CreateFileW(filename, access, sharing, security, disposition, flags, template);
            var error = Marshal.GetLastWin32Error();
            try
            {
                // OPEN_ALWAYS sets last error to zero only when it created the file. An open of
                // an existing name carries no creation authority, even when SQLite writes to it.
                if (handle != new IntPtr(-1) && (disposition == CreateNew || (disposition == OpenAlways && error == 0)) &&
                    Marshal.PtrToStringUni(filename) is { } nativePath &&
                    paths.TryGetValue(WithoutDevicePrefix(nativePath), out var path))
                {
                    using var borrowed = new SafeFileHandle(handle, ownsHandle: false);
                    ShadowStartRunRegistry.SaveCandidateSidecar(record,
                        new(run.Id, creator, path, ShadowStartReclaimer.ReadIdentity(borrowed)));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
            {
                // SQLite still owns the handle. Cleanup will defer if it cannot verify this identity.
            }
            finally
            {
                SetLastError((uint)error);
            }
            return handle;
        }
    }

    private static string WithoutDevicePrefix(string path) =>
        path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase) ? @"\\" + path[8..] :
        path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..] : path;

    // sqlite3_vfs through xSetSystemCall. Later members are irrelevant to the hook.
    [StructLayout(LayoutKind.Sequential)]
    private struct SqliteVfs
    {
        internal int Version, FileSize, MaxPath;
        internal nint Next, Name, AppData, Open, Delete, Access, FullPath, DlOpen, DlError, DlSym, DlClose;
        internal nint Randomness, Sleep, CurrentTime, GetLastError, CurrentTimeInt64, SetSystemCall;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int SetSystemCallCallback(nint vfs, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, nint function);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint CreateFileCallback(nint filename, uint access, uint sharing, nint security,
        uint disposition, uint flags, nint template);

    [DllImport("e_sqlite3", EntryPoint = "sqlite3_vfs_find", CallingConvention = CallingConvention.Cdecl)]
    private static extern nint FindVfs([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern nint CreateFileW(nint filename, uint access, uint sharing, nint security,
        uint disposition, uint flags, nint template);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern void SetLastError(uint error);
}
