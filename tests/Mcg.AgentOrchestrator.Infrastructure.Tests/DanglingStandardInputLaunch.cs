using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Win32.SafeHandles;

// Intentionally bypasses the production launcher's valid-stdin invariant. Never mutates host std handles.
internal static class DanglingStandardInputLaunch
{
    private const uint CreateNoWindow = 0x08000000, ExtendedStartupInfo = 0x80000, UnicodeEnvironment = 0x400;
    private const int UseStdHandles = 0x100, HandleListAttribute = 0x20002;

    internal static TestChildProcessCapture.Result Run(ProcessStartInfo info, string root,
        int? danglingHandleValue, bool inheritInput)
    {
        var stdoutPath = Path.Combine(root, "child.out");
        var stderrPath = Path.Combine(root, "child.err");
        using var stdout = File.OpenHandle(stdoutPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
        using var stderr = File.OpenHandle(stderrPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
        using var stdin = CreateFileW("NUL", 0x80000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (stdin.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        var input = danglingHandleValue.HasValue ? new IntPtr(danglingHandleValue.Value) : stdin.DangerousGetHandle();
        // Relocate capture handles if a sweep value equals one: the chosen stdin must be absent in the child.
        using var output = DuplicateAwayFrom(stdout, input);
        using var error = DuplicateAwayFrom(stderr, input);
        var handles = inheritInput
            ? new[] { output.DangerousGetHandle(), error.DangerousGetHandle(), input }
            : new[] { output.DangerousGetHandle(), error.DangerousGetHandle() };
        var size = IntPtr.Zero;
        _ = InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
        if (size == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        var attributes = Marshal.AllocHGlobal(size);
        var values = Marshal.AllocHGlobal(IntPtr.Size * handles.Length);
        var initialized = false;
        try
        {
            if (!InitializeProcThreadAttributeList(attributes, 1, 0, ref size))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            initialized = true;
            Marshal.Copy(handles, 0, values, handles.Length);
            if (!UpdateProcThreadAttribute(attributes, 0, new IntPtr(HandleListAttribute), values,
                    new IntPtr(IntPtr.Size * handles.Length), IntPtr.Zero, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            var startup = new StartupInfoEx
            {
                cb = Marshal.SizeOf<StartupInfoEx>(), dwFlags = UseStdHandles,
                hStdInput = input, hStdOutput = handles[0], hStdError = handles[1], attributes = attributes
            };
            ProcessInformation created;
            // Cooperate with Process.Start so the short inheritance window cannot leak capture handles.
            lock (HandleListProcessLauncher.InheritanceLock)
            {
                foreach (var handle in new[] { output, error, stdin }) SetInheritance(handle, false);
                try
                {
                    SetInheritance(output, true);
                    SetInheritance(error, true);
                    SetInheritance(stdin, inheritInput);
                    if (!CreateProcessW(info.FileName, new StringBuilder($"\"{info.FileName}\" {info.Arguments}"),
                            IntPtr.Zero, IntPtr.Zero, true, CreateNoWindow | ExtendedStartupInfo | UnicodeEnvironment,
                            HandleListProcessLauncher.EnvironmentBlock(info.Environment), info.WorkingDirectory,
                            ref startup, out created))
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                }
                finally
                {
                    SetInheritance(output, false);
                    SetInheritance(error, false);
                    SetInheritance(stdin, false);
                }
            }
            using var process = new SafeFileHandle(created.process, ownsHandle: true);
            using var thread = new SafeFileHandle(created.thread, ownsHandle: true);
            var wait = WaitForSingleObject(process, 180_000);
            if (wait != 0)
            {
                // Act on the owned process handle, never a potentially reused pid.
                if (!TerminateProcess(process, 124))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not terminate cmd after its process-exit event failed.");
                _ = WaitForSingleObject(process, 30_000);
                throw new InvalidOperationException($"cmd process-exit event was not signaled; wait=0x{wait:X}.");
            }
            if (!GetExitCodeProcess(process, out var exitCode))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            return new(unchecked((int)exitCode), ReadCapture(stdoutPath), ReadCapture(stderrPath));
        }
        finally
        {
            if (initialized) DeleteProcThreadAttributeList(attributes);
            Marshal.FreeHGlobal(values);
            Marshal.FreeHGlobal(attributes);
        }
    }

    private static string ReadCapture(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static SafeFileHandle DuplicateAwayFrom(SafeFileHandle source, IntPtr excluded)
    {
        var current = GetCurrentProcess();
        if (!DuplicateHandle(current, source, current, out var copy, 0, false, 2))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        if (copy.DangerousGetHandle() != excluded) return copy;
        using (copy) return DuplicateAwayFrom(copy, excluded);
    }

    private static void SetInheritance(SafeFileHandle handle, bool inherit)
    {
        if (!SetHandleInformation(handle, 1, inherit ? 1u : 0u))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfoEx
    {
        internal int cb;
        internal IntPtr reserved, desktop, title;
        internal int x, y, xSize, ySize, xCountChars, yCountChars, fillAttribute, dwFlags;
        internal short showWindow, reservedSize;
        internal IntPtr reservedBytes, hStdInput, hStdOutput, hStdError, attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        internal IntPtr process, thread;
        internal int processId, threadId;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security,
        uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DuplicateHandle(IntPtr sourceProcess, SafeFileHandle source,
        IntPtr targetProcess, out SafeFileHandle target, uint access, bool inherit, uint options);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetHandleInformation(SafeFileHandle handle, uint mask, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr size);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute,
        IntPtr value, IntPtr size, IntPtr previous, IntPtr returnedSize);
    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(IntPtr list);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcessW(string application, StringBuilder command, IntPtr processSecurity,
        IntPtr threadSecurity, bool inherit, uint flags, string environment, string directory,
        ref StartupInfoEx startup, out ProcessInformation process);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(SafeFileHandle handle, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeProcess(SafeFileHandle process, out uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateProcess(SafeFileHandle process, uint exitCode);
}
