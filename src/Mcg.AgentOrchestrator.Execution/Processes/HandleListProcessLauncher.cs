using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Mcg.AgentOrchestrator.Infrastructure;

// Owns the Windows CreateProcess boundary. Every inherited object is explicitly listed;
// callers own borrowed handles and must keep them alive until Create returns.
internal static class HandleListProcessLauncher
{
    internal const uint Suspended = 4, UnicodeEnvironment = 0x400, ExtendedStartup = 0x80000;
    internal const uint CreateNoWindow = 0x08000000;
    private const int UseStdHandles = 0x100, ProcThreadAttributeHandleList = 0x00020002;
    internal readonly record struct Creation(IntPtr ProcessHandle, IntPtr ThreadHandle, int ProcessId);
    internal readonly record struct LaunchOutcome(bool Created, IntPtr ProcessHandle, IntPtr ThreadHandle, int ProcessId, int NativeError);

    // Process.Start uses this monitor around its all-handles-inheriting CreateProcess call.
    // Sharing it also covers raw managed starts in fixtures and libraries. A missing runtime
    // field fails closed, before any pipe is made inheritable; do not substitute another lock.
    internal static object InheritanceLock => RuntimeCreateProcessLock(null);

    [UnsafeAccessor(UnsafeAccessorKind.StaticField, Name = "s_createProcessLock")]
    private static extern ref object RuntimeCreateProcessLock(Process? declaringType);

    internal static void MakeInheritable(SafeHandle handle)
    {
        if (!SetHandleInformation(handle, 1, 1)) throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    // Detached successors borrow their log handles; ownership stays with the caller.
    internal static LaunchOutcome Launch(string application, string commandLine, string environment,
        string workingDirectory, uint creationFlags, IntPtr stdoutHandle, IntPtr stderrHandle)
    {
        ProcessTreeGuiSuppression.EnsureLaunchSuppression(LaunchLockEntryPoint.AcquireConsoleForChildSpawn,
            ProcessTreeGuiSuppression.ErrorModeAccess.Native);
        var before = Stopwatch.GetTimestamp();
        long createTicks = 0;
        try
        {
            var result = Create(application, commandLine, environment, workingDirectory, creationFlags,
                IntPtr.Zero, stdoutHandle, stderrHandle, [stdoutHandle, stderrHandle], out createTicks);
            LaunchLockChildCpuTracker.Track(result.ProcessHandle);
            return new(true, result.ProcessHandle, result.ThreadHandle, result.ProcessId, 0);
        }
        catch (Win32Exception failure)
        {
            return new(false, IntPtr.Zero, IntPtr.Zero, 0, failure.NativeErrorCode);
        }
        finally
        {
            LaunchLockTelemetry.Record(LaunchLockEntryPoint.AcquireConsoleForChildSpawn, 0,
                Stopwatch.GetTimestamp() - before, createTicks);
        }
    }

    // Compatibility for detached-host identity/event consumers, which have no redirected streams.
    internal static Process StartAttached(ProcessStartInfo info)
    {
        if (info.RedirectStandardInput || info.RedirectStandardOutput || info.RedirectStandardError)
            throw new InvalidOperationException("Attached Process compatibility cannot transfer redirected streams.");
        using var launched = Start(info);
        return launched.AttachProcess();
    }

    internal static HandleListLaunchedProcess StartSuppressed(ProcessStartInfo info, bool requestOwnConsole = false,
        LaunchLockEntryPoint entry = LaunchLockEntryPoint.Start,
        ProcessTreeGuiSuppression.ErrorModeAccess? errorModeAccess = null)
    {
        if (!OperatingSystem.IsWindows()) return Start(info, entry, errorModeAccess);
        var original = info.CreateNoWindow;
        try
        {
            info.CreateNoWindow = ChildConsoleLaunchPolicy.Prepare(requestOwnConsole).ChildCreateNoWindow;
            return Start(info, entry, errorModeAccess);
        }
        finally { info.CreateNoWindow = original; }
    }

    internal static HandleListLaunchedProcess Start(ProcessStartInfo info,
        LaunchLockEntryPoint entry = LaunchLockEntryPoint.Start,
        ProcessTreeGuiSuppression.ErrorModeAccess? errorModeAccess = null,
        SafeFileHandle? job = null, uint creationFlags = 0,
        Action<IReadOnlyList<IntPtr>>? windowObserver = null,
        IReadOnlyList<IntPtr>? additionalHandles = null,
        Func<IReadOnlyList<SafeFileHandle>>? acquireAdditionalHandles = null,
        Action<HandleListLaunchedProcess>? createdObserver = null)
    {
        var process = new HandleListLaunchedProcess { StartInfo = info };
        try { process.Start(entry, errorModeAccess, job, creationFlags, windowObserver, additionalHandles, acquireAdditionalHandles, createdObserver); return process; }
        catch { process.Dispose(); throw; }
    }

    internal static Creation Create(string? application, string commandLine, string environment,
        string? directory, uint flags, IntPtr stdin, IntPtr stdout, IntPtr stderr,
        IReadOnlyList<IntPtr> inheritedHandles, out long createTicks, SafeFileHandle? job = null)
    {
        createTicks = 0;
        var handles = inheritedHandles.Distinct().ToArray();
        if (handles.Any(h => h == IntPtr.Zero || h == new IntPtr(-1)))
            throw new ArgumentException("Inherited handle lists must contain real handles.", nameof(inheritedHandles));
        foreach (var standard in new[] { stdin, stdout, stderr })
            if (standard != IntPtr.Zero && !handles.Contains(standard))
                throw new ArgumentException("Every supplied standard handle must be listed.", nameof(inheritedHandles));
        var count = (handles.Length > 0 ? 1 : 0) + (job is not null ? 1 : 0);
        var size = IntPtr.Zero;
        var attributes = IntPtr.Zero;
        var handleValues = IntPtr.Zero;
        var jobValue = IntPtr.Zero;
        var initialized = false;
        var jobRef = false;
        try
        {
            if (count > 0)
            {
                _ = InitializeProcThreadAttributeList(IntPtr.Zero, count, 0, ref size);
                if (size == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
                attributes = Marshal.AllocHGlobal(size);
                if (!InitializeProcThreadAttributeList(attributes, count, 0, ref size))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                initialized = true;
                if (handles.Length > 0)
                {
                    handleValues = Marshal.AllocHGlobal(IntPtr.Size * handles.Length);
                    Marshal.Copy(handles, 0, handleValues, handles.Length);
                    SetAttribute(attributes, ProcThreadAttributeHandleList, handleValues, handles.Length);
                }
                if (job is not null)
                {
                    job.DangerousAddRef(ref jobRef);
                    jobValue = Marshal.AllocHGlobal(IntPtr.Size);
                    Marshal.WriteIntPtr(jobValue, job.DangerousGetHandle());
                    SetAttribute(attributes, 0x2000D, jobValue, 1);
                }
            }
            var startup = new StartupInfoEx
            {
                cb = Marshal.SizeOf<StartupInfoEx>(),
                dwFlags = stdin != IntPtr.Zero || stdout != IntPtr.Zero || stderr != IntPtr.Zero ? UseStdHandles : 0,
                hStdInput = stdin, hStdOutput = stdout, hStdError = stderr, lpAttributeList = attributes
            };
            // EXTENDED_STARTUPINFO_PRESENT is valid even with no attributes (owned no-capture path).
            var buffer = new StringBuilder(commandLine);
            var before = Stopwatch.GetTimestamp();
            ProcessInformation result;
            bool created;
            int nativeError;
            try
            {
                created = handles.Length > 0
                    ? CreateProcessW(application, buffer, IntPtr.Zero, IntPtr.Zero,
                        bInheritHandles: true, flags | ExtendedStartup, environment, directory, ref startup, out result)
                    : CreateProcessW(application, buffer, IntPtr.Zero, IntPtr.Zero,
                        bInheritHandles: false, flags | ExtendedStartup, environment, directory, ref startup, out result);
                nativeError = created ? 0 : Marshal.GetLastWin32Error();
            }
            finally { createTicks = Stopwatch.GetTimestamp() - before; }
            if (!created) throw new Win32Exception(nativeError);
            return new(result.hProcess, result.hThread, unchecked((int)result.dwProcessId));
        }
        finally
        {
            if (initialized) DeleteProcThreadAttributeList(attributes);
            Marshal.FreeHGlobal(jobValue);
            Marshal.FreeHGlobal(handleValues);
            Marshal.FreeHGlobal(attributes);
            if (jobRef) job!.DangerousRelease();
        }
    }

    private static void SetAttribute(IntPtr list, int attribute, IntPtr values, int count)
    {
        if (!UpdateProcThreadAttribute(list, 0, new IntPtr(attribute), values,
                new IntPtr(IntPtr.Size * count), IntPtr.Zero, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    internal static string EnvironmentBlock(IEnumerable<KeyValuePair<string, string?>> values) =>
        string.Concat(values.Where(p => p.Value is not null).OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
            .Select(p => p.Key + "=" + p.Value + '\0')) + "\0\0";

    internal static string CommandLine(ProcessStartInfo info)
    {
        var builder = new StringBuilder();
        AppendArgument(builder, info.FileName.Trim().Trim('"'));
        if (info.ArgumentList.Count > 0)
            foreach (var argument in info.ArgumentList) { builder.Append(' '); AppendArgument(builder, argument); }
        else if (!string.IsNullOrEmpty(info.Arguments)) builder.Append(' ').Append(info.Arguments);
        return builder.ToString();
    }

    // Windows CRT argv escaping, including embedded quotes and trailing backslashes.
    private static void AppendArgument(StringBuilder builder, string argument)
    {
        if (argument.Length > 0 && !argument.Any(c => char.IsWhiteSpace(c) || c == '"'))
        { builder.Append(argument); return; }
        builder.Append('"');
        var slashes = 0;
        foreach (var c in argument)
        {
            if (c == '\\') { slashes++; continue; }
            builder.Append('\\', c == '"' ? slashes * 2 + 1 : slashes).Append(c);
            slashes = 0;
        }
        builder.Append('\\', slashes * 2).Append('"');
    }

    internal static string ResolveApplication(string fileName)
    {
        var name = fileName.Trim().Trim('"');
        if (Path.IsPathRooted(name)) return name;
        var directories = new[] { Path.GetDirectoryName(Environment.ProcessPath)!, Environment.CurrentDirectory }
            .Concat((Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator));
        foreach (var directory in directories)
        {
            if (string.IsNullOrWhiteSpace(directory)) continue;
            var path = Path.GetFullPath(Path.Combine(directory.Trim('"'), name));
            if (File.Exists(path)) return path;
            if (!Path.HasExtension(name) && File.Exists(path + ".exe")) return path + ".exe";
        }
        return name;
    }

    internal static SafeFileHandle NullHandle()
    {
        var security = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>() };
        var handle = CreateFileW("NUL", 0xC0000000, 3, ref security, 3, 0x80, IntPtr.Zero);
        if (handle.IsInvalid) { var error = Marshal.GetLastWin32Error(); handle.Dispose(); throw new Win32Exception(error); }
        return handle;
    }

    internal static (SafeFileHandle Parent, SafeFileHandle Child) Pipe(bool input)
    {
        // Both ends stay private until the caller holds InheritanceLock.
        if (!CreatePipe(out var read, out var write, IntPtr.Zero, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
        var parent = input ? write : read;
        var child = input ? read : write;
        return (parent, child);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes { internal int Length; internal IntPtr Descriptor; [MarshalAs(UnmanagedType.Bool)] internal bool Inherit; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfoEx
    {
        internal int cb; internal string? reserved, desktop, title;
        internal int x, y, xSize, ySize, xChars, yChars, fill, dwFlags;
        internal short show, reservedSize; internal IntPtr reservedBytes;
        internal IntPtr hStdInput, hStdOutput, hStdError, lpAttributeList;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation { internal IntPtr hProcess, hThread; internal uint dwProcessId, dwThreadId; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcessW(string? application, StringBuilder commandLine, IntPtr processAttributes,
        IntPtr threadAttributes, bool bInheritHandles, uint flags, string environment, string? directory,
        ref StartupInfoEx startup, out ProcessInformation information);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr size);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute, IntPtr value,
        IntPtr size, IntPtr previous, IntPtr returnedSize);
    [DllImport("kernel32.dll")] private static extern void DeleteProcThreadAttributeList(IntPtr list);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, IntPtr attributes, uint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetHandleInformation(SafeHandle handle, uint mask, uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, ref SecurityAttributes security,
        uint disposition, uint flags, IntPtr template);
}
