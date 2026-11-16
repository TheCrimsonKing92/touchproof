using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Mcg.AgentOrchestrator.Infrastructure;

// Git for Windows obtains cwd from a normalized handle path, including its volume spelling.
internal static class GitWindowsDirectoryPath
{
    internal static bool HasExactSpelling(string directory)
    {
        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        var root = Path.GetPathRoot(fullPath);
        // Other volume forms need their own proof of Git's output conventions.
        if (root is not { Length: 3 } || root[0] is < 'A' or > 'Z' || root[1] != ':' ||
            root[2] != '\\' || new DriveInfo(root).DriveType != DriveType.Fixed) return false;

        const uint shareReadWriteDelete = 1 | 2 | 4;
        const uint openExisting = 3;
        const uint backupSemantics = 0x02000000;
        using var handle = CreateFileW(fullPath, 0, shareReadWriteDelete, IntPtr.Zero,
            openExisting, backupSemantics, IntPtr.Zero);
        if (handle.IsInvalid) return false;

        var buffer = new StringBuilder(512);
        var length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, 0);
        if (length == 0) return false;
        if (length >= buffer.Capacity)
        {
            buffer.EnsureCapacity(checked((int)length + 1));
            length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, 0);
            if (length == 0 || length >= buffer.Capacity) return false;
        }
        // Comparing the complete path also refuses SUBST drives and any redirected ancestor.
        return string.Equals(buffer.ToString(), @"\\?\" + fullPath, StringComparison.Ordinal);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern SafeFileHandle CreateFileW(string fileName, uint desiredAccess, uint shareMode,
        IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle file, StringBuilder path,
        uint capacity, uint flags);
}
