using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal static partial class ProcessTreeGuiSuppression
{
    internal const uint FailCriticalErrors = 0x0001;
    internal const uint NoGpFaultErrorBox = 0x0002;
    internal const uint NoOpenFileErrorBox = 0x8000;
    internal const uint SuppressedErrorModeFlags = FailCriticalErrors | NoGpFaultErrorBox | NoOpenFileErrorBox;

    // Excludes all-handles-inheriting launches from temporary native inheritable handles.
    private static readonly object WindowsLaunchLock = new();
    private static int processErrorModeInitialized;

    internal sealed record ErrorModeAccess(Func<uint> Get, Func<uint, uint> Set, Action<string>? Diagnostic = null)
    {
        internal static readonly ErrorModeAccess Native = new(Windows.GetErrorMode, Windows.SetErrorMode);
    }

    internal static void InitializeProcessErrorMode()
    {
        if (OperatingSystem.IsWindows())
            InitializeProcessErrorMode(ErrorModeAccess.Native, ref processErrorModeInitialized);
    }

    internal static void InitializeProcessErrorMode(ErrorModeAccess access, ref int initialized)
    {
        lock (WindowsLaunchLock)
        {
            if (initialized != 0) return;
            _ = access.Set(access.Get() | SuppressedErrorModeFlags);
            initialized = 1;
        }
    }

    private static void EnsureLaunchSuppression(LaunchLockEntryPoint entry, ErrorModeAccess access)
    {
        try
        {
            if ((access.Get() & SuppressedErrorModeFlags) == SuppressedErrorModeFlags) return;
            lock (WindowsLaunchLock)
            {
                var before = access.Get();
                if ((before & SuppressedErrorModeFlags) == SuppressedErrorModeFlags) return;
                var after = before | SuppressedErrorModeFlags;
                _ = access.Set(after);
                ReportErrorModeDiagnostic(access, $"PROCESS_ERROR_MODE_REPAIRED entry={entry} before={before} after={after}");
            }
        }
        catch (Exception ex)
        {
            ReportErrorModeDiagnostic(access, $"PROCESS_ERROR_MODE_REPAIR_FAILED entry={entry} error={ex.GetType().Name}");
        }
    }

    private static void ReportErrorModeDiagnostic(ErrorModeAccess access, string message)
    {
        // A defensive repair/diagnostic failure must not prevent child creation.
        try
        {
            if (access.Diagnostic is { } diagnostic) diagnostic(message);
            else Console.Error.WriteLine(message);
        }
        catch (Exception) { }
    }

    internal sealed class ConsoleSpawnScope : IDisposable
    {
        private readonly Action? onDispose;
        private readonly LaunchLockEntryPoint? entryPoint;
        private readonly long waitTicks;
        private readonly long acquiredTimestamp;
        private long childCreateTicks;
        private bool disposed;

        internal ConsoleSpawnScope(bool childConsolePolicyApplied, uint childCreationFlags = 0,
            Action? onDispose = null, LaunchLockEntryPoint? entryPoint = null,
            long waitTicks = 0, long acquiredTimestamp = 0)
        {
            ChildConsolePolicyApplied = childConsolePolicyApplied;
            ChildCreationFlags = childCreationFlags;
            this.onDispose = onDispose;
            this.entryPoint = entryPoint;
            this.waitTicks = waitTicks;
            this.acquiredTimestamp = acquiredTimestamp;
        }

        internal bool ChildConsolePolicyApplied { get; }
        internal uint ChildCreationFlags { get; }
        internal void RecordChildCreateTicks(long ticks) => childCreateTicks = ticks;

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            var holdEnded = Stopwatch.GetTimestamp();
            try { onDispose?.Invoke(); }
            finally
            {
                if (entryPoint is { } entry)
                    LaunchLockTelemetry.Record(entry, waitTicks,
                        onDispose is null ? 0 : holdEnded - acquiredTimestamp, childCreateTicks);
            }
        }
    }

    internal static ConsoleSpawnScope AcquireConsoleForChildSpawn() =>
        AcquireConsoleForChildSpawn(ErrorModeAccess.Native);

    internal static ConsoleSpawnScope AcquireConsoleForChildSpawn(ErrorModeAccess access)
    {
        if (!OperatingSystem.IsWindows()) return new(false);
        var policy = ChildConsoleLaunchPolicy.Prepare();
        return AcquireSpawnLock(LaunchLockEntryPoint.AcquireConsoleForChildSpawn,
            policy.ChildCreateNoWindow, policy.ChildCreationFlags, access);
    }

    internal static ConsoleSpawnScope AcquireSuppressedChildSpawn(bool requestOwnConsole = false) =>
        AcquireSuppressedChildSpawn(requestOwnConsole, ErrorModeAccess.Native);

    internal static ConsoleSpawnScope PrepareSuppressedChildSpawn(bool requestOwnConsole, bool requiresLaunchLock,
        ErrorModeAccess? access = null)
    {
        access ??= ErrorModeAccess.Native;
        if (requiresLaunchLock) return AcquireSuppressedChildSpawn(requestOwnConsole, access);
        if (!OperatingSystem.IsWindows()) return new(false);
        EnsureLaunchSuppression(LaunchLockEntryPoint.AcquireSuppressedChildSpawn, access);
        var policy = ChildConsoleLaunchPolicy.Prepare(requestOwnConsole);
        return new(policy.ChildCreateNoWindow, policy.ChildCreationFlags,
            entryPoint: LaunchLockEntryPoint.AcquireSuppressedChildSpawn);
    }

    internal static ConsoleSpawnScope AcquireSuppressedChildSpawn(bool requestOwnConsole, ErrorModeAccess access)
    {
        if (!OperatingSystem.IsWindows()) return new(false);
        var policy = ChildConsoleLaunchPolicy.Prepare(requestOwnConsole);
        return AcquireSpawnLock(LaunchLockEntryPoint.AcquireSuppressedChildSpawn,
            policy.ChildCreateNoWindow, policy.ChildCreationFlags, access);
    }

    internal static ConsoleSpawnScope AcquireErrorModeForChildSpawn() =>
        AcquireErrorModeForChildSpawn(ErrorModeAccess.Native);

    internal static ConsoleSpawnScope AcquireErrorModeForChildSpawn(ErrorModeAccess access)
    {
        if (!OperatingSystem.IsWindows()) return new(false);
        return AcquireSpawnLock(LaunchLockEntryPoint.AcquireErrorModeForChildSpawn, false, 0, access);
    }

    private static ConsoleSpawnScope AcquireSpawnLock(LaunchLockEntryPoint entry, bool consoleApplied,
        uint creationFlags, ErrorModeAccess access)
    {
        EnsureLaunchSuppression(entry, access);
        var waitStarted = Stopwatch.GetTimestamp();
        Monitor.Enter(WindowsLaunchLock);
        var acquired = Stopwatch.GetTimestamp();
        try
        {
            return new(consoleApplied, creationFlags, () => Monitor.Exit(WindowsLaunchLock),
                entry, acquired - waitStarted, acquired);
        }
        catch
        {
            Monitor.Exit(WindowsLaunchLock);
            throw;
        }
    }

    public static Process Start(ProcessStartInfo startInfo) => Start(startInfo, ErrorModeAccess.Native);

    internal static Process Start(ProcessStartInfo startInfo, ErrorModeAccess access)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        if (!OperatingSystem.IsWindows())
            return Process.Start(startInfo) ?? throw new InvalidOperationException($"Failed to start process: {startInfo.FileName}");
        if (startInfo.UseShellExecute)
            throw new InvalidOperationException("Process tree GUI suppression requires UseShellExecute=false.");

        var originalCreateNoWindow = startInfo.CreateNoWindow;
        try
        {
            startInfo.CreateNoWindow = ChildConsoleLaunchPolicy.Prepare().ChildCreateNoWindow;
            return StartWithLaunchLock(startInfo, LaunchLockEntryPoint.Start, access)
                ?? throw new InvalidOperationException($"Failed to start process: {startInfo.FileName}");
        }
        finally { startInfo.CreateNoWindow = originalCreateNoWindow; }
    }

    // Unlike Start, this preserves the caller's console policy and null/false result contract.
    internal static Process? StartWithLaunchLock(ProcessStartInfo startInfo,
        LaunchLockEntryPoint entry = LaunchLockEntryPoint.Start, ErrorModeAccess? access = null,
        Action? lockContended = null) =>
        CreateWithLaunchLock(() => Process.Start(startInfo), startInfo.UseShellExecute, entry, access, lockContended);

    internal static bool StartWithLaunchLock(Process process) =>
        CreateWithLaunchLock(process.Start, process.StartInfo.UseShellExecute, LaunchLockEntryPoint.Start, null, null);

    private static T CreateWithLaunchLock<T>(Func<T> create, bool shellExecute, LaunchLockEntryPoint entry,
        ErrorModeAccess? access, Action? lockContended)
    {
        if (!OperatingSystem.IsWindows()) return create();
        EnsureLaunchSuppression(entry, access ?? ErrorModeAccess.Native);
        // ShellExecute does not inherit the process's inheritable handle set.
        if (shellExecute) return create();
        var waitStarted = Stopwatch.GetTimestamp();
        if (!Monitor.TryEnter(WindowsLaunchLock))
        {
            lockContended?.Invoke();
            Monitor.Enter(WindowsLaunchLock);
        }
        var acquired = Stopwatch.GetTimestamp();
        try { return create(); }
        finally
        {
            var holdEnded = Stopwatch.GetTimestamp();
            Monitor.Exit(WindowsLaunchLock);
            LaunchLockTelemetry.Record(entry, acquired - waitStarted,
                holdEnded - acquired, holdEnded - acquired);
        }
    }

    private static class Windows
    {
        [DllImport("kernel32.dll")]
        internal static extern uint GetErrorMode();
        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern uint SetErrorMode(uint mode);
    }
}
