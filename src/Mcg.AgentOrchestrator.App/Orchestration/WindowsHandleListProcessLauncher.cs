using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Diagnostics;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class WindowsHandleListProcessLauncher
{
    // This boundary inherits only the designated logs; no ambient standard handles.
    internal static DetachedProcessCreationOutcome Launch(
        string application, string commandLine, string environment, string workingDirectory,
        uint creationFlags, IntPtr stdoutHandle, IntPtr stderrHandle)
    {
        ProcessTreeGuiSuppression.EnsureLaunchSuppression(LaunchLockEntryPoint.AcquireConsoleForChildSpawn,
            ProcessTreeGuiSuppression.ErrorModeAccess.Native);
        var before = Stopwatch.GetTimestamp();
        long createTicks = 0;
        try
        {
            var result = HandleListProcessLauncher.Create(application, commandLine, environment,
                workingDirectory, creationFlags, IntPtr.Zero, stdoutHandle, stderrHandle, [stdoutHandle, stderrHandle], out createTicks);
            return new DetachedProcessCreationOutcome(true, result.ProcessHandle, result.ThreadHandle, result.ProcessId, 0);
        }
        catch (Win32Exception failure)
        {
            return new DetachedProcessCreationOutcome(false, IntPtr.Zero, IntPtr.Zero, 0, failure.NativeErrorCode);
        }
        finally
        {
            var elapsed = Stopwatch.GetTimestamp() - before;
            LaunchLockTelemetry.Record(LaunchLockEntryPoint.AcquireConsoleForChildSpawn, 0, elapsed, createTicks);
        }
    }

    internal static string BuildEnvironmentBlock(IReadOnlyDictionary<string, string> values)
    {
        var builder = new StringBuilder();
        foreach (var pair in values.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            builder.Append(pair.Key);
            builder.Append('=');
            builder.Append(pair.Value);
            builder.Append('\0');
        }
        builder.Append('\0');
        return builder.ToString();
    }

    internal static IntPtr CreateInheritedOutputFile(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        var securityAttributes = new WindowsSecurityAttributes
        {
            nLength = Marshal.SizeOf<WindowsSecurityAttributes>(),
            bInheritHandle = true
        };

        var handle = CreateFileW(
            path,
            WindowsFileAccess.GenericWrite,
            WindowsFileShare.Read | WindowsFileShare.Delete,
            ref securityAttributes,
            WindowsCreationDisposition.CreateAlways,
            WindowsFileFlags.FileAttributeNormal,
            IntPtr.Zero);
        if (handle == new IntPtr(-1))
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"Failed to open conduct loop successor log file: {path}");

        return handle;
    }

    [Flags]
    internal enum WindowsCreationFlags : uint
    {
        CreateNewProcessGroup = 0x00000200,
        CreateUnicodeEnvironment = 0x00000400,
        CreateBreakawayFromJob = 0x01000000,
        CreateNoWindow = 0x08000000,
        ExtendedStartupInfoPresent = 0x00080000
    }

    private const int ProcThreadAttributeHandleList = 0x00020002;

    [Flags]
    private enum WindowsStartupInfoFlags : int
    {
        UseStdHandles = 0x00000100
    }

    [Flags]
    private enum WindowsFileAccess : uint
    {
        GenericWrite = 0x40000000
    }

    [Flags]
    private enum WindowsFileShare : uint
    {
        Read = 0x00000001,
        Delete = 0x00000004
    }

    private enum WindowsCreationDisposition : uint
    {
        CreateAlways = 2
    }

    [Flags]
    private enum WindowsFileFlags : uint
    {
        FileAttributeNormal = 0x00000080
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowsSecurityAttributes
    {
        public int nLength;
        public IntPtr lpSecurityDescriptor;
        [MarshalAs(UnmanagedType.Bool)]
        public bool bInheritHandle;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowsProcessStartupInfo
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowsProcessInformation
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public uint dwProcessId;
        public uint dwThreadId;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowsProcessStartupInfoEx
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
        public IntPtr lpAttributeList;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateFileW(
        string lpFileName,
        WindowsFileAccess dwDesiredAccess,
        WindowsFileShare dwShareMode,
        ref WindowsSecurityAttributes lpSecurityAttributes,
        WindowsCreationDisposition dwCreationDisposition,
        WindowsFileFlags dwFlagsAndAttributes,
        IntPtr hTemplateFile);
}
