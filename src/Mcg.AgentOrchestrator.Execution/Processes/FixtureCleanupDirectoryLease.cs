using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Mcg.AgentOrchestrator.Infrastructure;

// Keep directory names stable while using path-based enumeration and nonrecursive deletes.
internal sealed class FixtureCleanupDirectoryLease : IDisposable
{
    private readonly List<SafeFileHandle> handles = [];

    internal FixtureCleanupDirectoryLease(string path, bool ancestors = false)
    {
        try
        {
            var paths = new Stack<string>();
            for (string? current = Path.GetFullPath(path); current is not null;
                 current = ancestors ? Path.GetDirectoryName(current) : null)
                paths.Push(current);
            foreach (var directory in paths)
            {
                if (OperatingSystem.IsWindows())
                {
                    // Non-inheritable, OPEN_REPARSE_POINT, BACKUP_SEMANTICS; deny delete sharing.
                    var handle = CreateFile(IcaclsIntegrityLabeler.NativeFilePath(directory), 1, 3,
                        IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
                    if (handle.IsInvalid)
                    {
                        var error = Marshal.GetLastWin32Error();
                        handle.Dispose();
                        throw new Win32Exception(error);
                    }
                    handles.Add(handle);
                }
                if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Fixture cleanup refuses a reparse-point ancestor.");
            }
        }
        catch { Dispose(); throw; }
    }

    public void Dispose()
    {
        for (var i = handles.Count - 1; i >= 0; i--) handles[i].Dispose();
        handles.Clear();
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share,
        IntPtr security, uint creation, uint flags, IntPtr template);
}
