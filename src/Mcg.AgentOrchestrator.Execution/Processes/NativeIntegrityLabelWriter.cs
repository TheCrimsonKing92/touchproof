using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Mcg.AgentOrchestrator.Infrastructure;

// Owns one label-only native write operation and its root-first, fail-fast traversal.
internal static class NativeIntegrityLabelWriter
{
    internal sealed record Result(bool Succeeded, string? FailingPath, int Win32Error,
        string? Reason, int EntriesLabeled, int ReparsePointsSkipped);

    internal static Result Write(string path, string level, bool recursive)
    {
        var entriesLabeled = 0;
        var reparsePointsSkipped = 0;
        var currentPath = path;
        var reason = "set-label-failed";
        IntPtr sid = IntPtr.Zero;
        IntPtr plainAcl = IntPtr.Zero;
        IntPtr inheritableAcl = IntPtr.Zero;
        Result Failure(int error, string failureReason) => new(false, currentPath,
            error == 0 ? 31 : error, failureReason, entriesLabeled, reparsePointsSkipped);

        if (level is not ("L" or "M" or "(OI)(CI)L" or "(OI)(CI)M"))
            return Failure(87, "invalid-level");
        if (!OperatingSystem.IsWindows())
            return Failure(50, "unsupported-platform");

        try
        {
            var rootAttributes = File.GetAttributes(path);
            if (recursive && IsDirectory(rootAttributes) && IsReparsePoint(rootAttributes))
                return Failure(4395, "root-reparse-point");

            var sidText = level.EndsWith('L') ? "S-1-16-4096" : "S-1-16-8192";
            if (!ConvertStringSidToSidW(sidText, out sid))
                return Failure(Marshal.GetLastWin32Error(), reason);
            plainAcl = CreateAcl(sid, 0);
            var inheritable = level.StartsWith("(OI)(CI)", StringComparison.Ordinal);
            if (inheritable)
                inheritableAcl = CreateAcl(sid, 0x01 | 0x02);

            var pending = new Stack<string>();
            pending.Push(path);
            var enumeration = new EnumerationOptions
            {
                RecurseSubdirectories = false,
                AttributesToSkip = 0,
                IgnoreInaccessible = false
            };
            while (pending.TryPop(out currentPath))
            {
                reason = "set-label-failed";
                var attributes = File.GetAttributes(currentPath);
                if (recursive && IsReparsePoint(attributes))
                {
                    reparsePointsSkipped++;
                    continue;
                }

                // icacls ignores inheritable level requests on files, preserving their
                // explicit labels. Directory writes propagate to inherited file labels.
                if (inheritable && !IsDirectory(attributes))
                    continue;
                var acl = inheritable ? inheritableAcl : plainAcl;
                var error = SetNamedSecurityInfoW(IcaclsIntegrityLabeler.NativeFilePath(currentPath),
                    1, 0x00000010, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, acl);
                if (error != 0)
                    return Failure(checked((int)error), reason);
                entriesLabeled++;

                if (!recursive || !IsDirectory(attributes))
                    continue;
                reason = "enumerate-failed";
                foreach (var child in Directory.EnumerateFileSystemEntries(currentPath, "*", enumeration))
                    pending.Push(child);
            }
            return new(true, null, 0, null, entriesLabeled, reparsePointsSkipped);
        }
        catch (Exception exception)
        {
            var error = exception switch
            {
                Win32Exception native => native.NativeErrorCode,
                UnauthorizedAccessException => 5,
                DirectoryNotFoundException => 3,
                FileNotFoundException => 2,
                DllNotFoundException => 126,
                EntryPointNotFoundException => 127,
                ArgumentException => 87,
                IOException io when (io.HResult & 0xffff0000u) == 0x80070000u => io.HResult & 0xffff,
                _ => 31 // ERROR_GEN_FAILURE when no underlying Win32 error is available.
            };
            return Failure(error, exception is DllNotFoundException or EntryPointNotFoundException
                ? "native-unavailable" : reason);
        }
        finally
        {
            if (inheritableAcl != IntPtr.Zero) Marshal.FreeHGlobal(inheritableAcl);
            if (plainAcl != IntPtr.Zero) Marshal.FreeHGlobal(plainAcl);
            if (sid != IntPtr.Zero) _ = LocalFree(sid);
        }
    }

    private static bool IsDirectory(FileAttributes attributes) => (attributes & FileAttributes.Directory) != 0;
    private static bool IsReparsePoint(FileAttributes attributes) => (attributes & FileAttributes.ReparsePoint) != 0;

    private static IntPtr CreateAcl(IntPtr sid, uint flags)
    {
        // ACL header + mandatory ACE header/access mask + integrity SID.
        var length = checked(8 + 8 + GetLengthSid(sid));
        var acl = Marshal.AllocHGlobal(length);
        try
        {
            if (!InitializeAcl(acl, length, 2) || !AddMandatoryAce(acl, 2, flags, 1, sid))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            return acl;
        }
        catch
        {
            Marshal.FreeHGlobal(acl);
            throw;
        }
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ConvertStringSidToSidW(string text, out IntPtr sid);

    [DllImport("advapi32.dll")]
    private static extern int GetLengthSid(IntPtr sid);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InitializeAcl(IntPtr acl, int length, uint revision);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AddMandatoryAce(IntPtr acl, uint revision, uint flags, uint policy, IntPtr sid);

    // LABEL_SECURITY_INFORMATION replaces labels while preserving the DACL and
    // unrelated SACL entries; never request an entire SACL replacement here.
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint SetNamedSecurityInfoW(string path, int objectType, uint information,
        IntPtr owner, IntPtr group, IntPtr dacl, IntPtr sacl);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
