using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Mcg.AgentOrchestrator.Infrastructure;

// Owns the native process handle and parent pipe ends. Dispose closes them without killing.
// A managed Process is materialized only for low-volume compatibility operations/tree kill.
internal sealed class HandleListLaunchedProcess : IDisposable
{
    private SafeProcessHandle? handle;
    private Process? attached;
    private Process? portable;
    private StreamReader? output, error;
    private StreamWriter? input;
    private Task? outputLines, errorLines;
    private bool raisingEvents;
    internal HandleListLaunchedProcess() { }
    internal HandleListLaunchedProcess(Process process) { portable = process; StartInfo = process.StartInfo; }
    internal sealed record Line(string? Data);
    internal event Action<object?, Line>? OutputDataReceived;
    internal event Action<object?, Line>? ErrorDataReceived;
    internal event EventHandler? Exited;
    internal bool EnableRaisingEvents
    {
        get => raisingEvents;
        set
        {
            raisingEvents = value;
            if (handle is not null || portable is not null) EnableEvents();
        }
    }
    private void EnableEvents()
    {
        try
        {
            Attached.Exited += (_, e) => Exited?.Invoke(this, e);
            Attached.EnableRaisingEvents = raisingEvents;
        }
        catch (ArgumentException) when (HasExited)
        {
            if (raisingEvents) Exited?.Invoke(this, EventArgs.Empty);
        }
    }
    internal DateTime StartTime => Attached.StartTime;
    internal ProcessStartInfo StartInfo { get; set; } = new();
    private int id;
    internal int Id => portable?.Id ?? id;
    internal uint EffectiveCreationFlags { get; private set; }
    internal SafeProcessHandle SafeHandle => portable?.SafeHandle ?? handle ?? throw new InvalidOperationException("Process has not started.");
    internal IntPtr Handle => SafeHandle.DangerousGetHandle();
    internal StreamReader StandardOutput => portable?.StandardOutput ?? output ?? throw new InvalidOperationException("Standard output is not redirected.");
    internal StreamReader StandardError => portable?.StandardError ?? error ?? throw new InvalidOperationException("Standard error is not redirected.");
    internal StreamWriter StandardInput => portable?.StandardInput ?? input ?? throw new InvalidOperationException("Standard input is not redirected.");
    internal bool HasExited => portable?.HasExited ?? WaitForExit(0);
    internal int ExitCode
    {
        get
        {
            if (portable is not null) return portable.ExitCode;
            if (!HasExited) throw new InvalidOperationException("Process has not exited.");
            if (!GetExitCodeProcess(SafeHandle, out var code)) throw new Win32Exception(Marshal.GetLastWin32Error());
            return unchecked((int)code);
        }
    }
    internal TimeSpan TotalProcessorTime
    {
        get
        {
            if (portable is not null) return portable.TotalProcessorTime;
            if (!GetProcessTimes(SafeHandle, out _, out _, out var kernel, out var user))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            return TimeSpan.FromTicks(kernel + user);
        }
    }

    internal bool Start(LaunchLockEntryPoint entry = LaunchLockEntryPoint.Start,
        ProcessTreeGuiSuppression.ErrorModeAccess? errorModeAccess = null, SafeFileHandle? job = null,
        uint creationFlags = 0, Action<IReadOnlyList<IntPtr>>? windowObserver = null,
        IReadOnlyList<IntPtr>? additionalHandles = null,
        Func<IReadOnlyList<SafeFileHandle>>? acquireAdditionalHandles = null,
        Action<HandleListLaunchedProcess>? createdObserver = null)
    {
        if (handle is not null || portable is not null) throw new InvalidOperationException("Process already started.");
        if (!OperatingSystem.IsWindows())
        {
            portable = ProcessTreeGuiSuppression.StartWithLaunchLock(StartInfo);
            id = portable!.Id;
            return true;
        }
        if (StartInfo.UseShellExecute) throw new NotSupportedException("The handle-list launcher requires UseShellExecute=false.");
        if (StartInfo.ArgumentList.Count > 0 && StartInfo.Arguments.Length > 0)
            throw new InvalidOperationException("Arguments and ArgumentList cannot both be set.");
        ProcessTreeGuiSuppression.EnsureLaunchSuppression(entry, errorModeAccess ?? ProcessTreeGuiSuppression.ErrorModeAccess.Native);
        var application = HandleListProcessLauncher.ResolveApplication(StartInfo.FileName);
        var command = HandleListProcessLauncher.CommandLine(StartInfo);
        var environment = HandleListProcessLauncher.EnvironmentBlock(StartInfo.Environment);
        var children = new List<SafeFileHandle>();
        var parents = new List<SafeFileHandle>();
        var started = Stopwatch.GetTimestamp();
        long createTicks = 0;
        try
        {
            var redirected = StartInfo.RedirectStandardInput || StartInfo.RedirectStandardOutput || StartInfo.RedirectStandardError;
            IntPtr Slot(bool redirect, bool isInput)
            {
                if (!redirected) return IntPtr.Zero;
                if (!redirect)
                {
                    var nul = HandleListProcessLauncher.NullHandle(); children.Add(nul); return nul.DangerousGetHandle();
                }
                var pipe = HandleListProcessLauncher.Pipe(isInput);
                children.Add(pipe.Child); parents.Add(pipe.Parent);
                var stream = new FileStream(pipe.Parent, isInput ? FileAccess.Write : FileAccess.Read, 4096, isAsync: false);
                if (isInput)
                    input = new StreamWriter(stream, StartInfo.StandardInputEncoding ?? ConsoleEncoding(true), 4096) { AutoFlush = true };
                else if (output is null && StartInfo.RedirectStandardOutput)
                    output = new StreamReader(stream, StartInfo.StandardOutputEncoding ?? ConsoleEncoding(false), true, 4096);
                else
                    error = new StreamReader(stream, StartInfo.StandardErrorEncoding ?? ConsoleEncoding(false), true, 4096);
                return pipe.Child.DangerousGetHandle();
            }
            var stdin = Slot(StartInfo.RedirectStandardInput, true);
            var stdout = Slot(StartInfo.RedirectStandardOutput, false);
            var stderr = Slot(StartInfo.RedirectStandardError, false);
            if (acquireAdditionalHandles is not null) children.AddRange(acquireAdditionalHandles());
            var handles = children.Select(h => h.DangerousGetHandle()).Concat(additionalHandles ?? []).Distinct().ToArray();
            windowObserver?.Invoke(handles);
            EffectiveCreationFlags = creationFlags | HandleListProcessLauncher.UnicodeEnvironment | HandleListProcessLauncher.ExtendedStartup |
                (StartInfo.CreateNoWindow ? ChildConsoleLaunchPolicy.CreateNoWindow : 0) |
                (StartInfo.CreateNewProcessGroup ? 0x200u : 0);
            var created = HandleListProcessLauncher.Create(application, command, environment,
                string.IsNullOrWhiteSpace(StartInfo.WorkingDirectory) ? null : StartInfo.WorkingDirectory,
                EffectiveCreationFlags, stdin, stdout, stderr, handles, out createTicks, job);
            handle = new SafeProcessHandle(created.ProcessHandle, ownsHandle: true);
            using var thread = new SafeFileHandle(created.ThreadHandle, ownsHandle: true);
            id = created.ProcessId;
            createdObserver?.Invoke(this);
            if (raisingEvents) EnableEvents();
            return true;
        }
        catch
        {
            if (handle is not null)
            {
                try { Kill(entireProcessTree: true); WaitForExit(5000); }
                catch (Exception) { } // A failed creation observer must not strand its child.
            }
            input?.Dispose(); output?.Dispose(); error?.Dispose();
            foreach (var parent in parents) parent.Dispose();
            throw;
        }
        finally
        {
            foreach (var child in children) child.Dispose();
            LaunchLockTelemetry.Record(entry, 0, Stopwatch.GetTimestamp() - started, createTicks);
        }
    }

    private static Encoding ConsoleEncoding(bool stdin)
    {
        var codePage = stdin ? GetConsoleCP() : GetConsoleOutputCP();
        // Process's default console encoding uses the Windows ANSI page when there is no
        // console, and suppresses a UTF-8 preamble. Do not register a global encoding provider.
        if (codePage == 65001) return new UTF8Encoding(false);
        if (codePage == 0) codePage = GetACP();
        return CodePagesEncodingProvider.Instance.GetEncoding((int)codePage) ?? Encoding.GetEncoding((int)codePage);
    }

    internal Process AttachProcess()
    {
        var process = Process.GetProcessById(Id);
        try
        {
            // GetProcessById alone retains no handle. Process.ExitCode requires one,
            // even after WaitForExit succeeds; the native owner keeps this identity alive.
            _ = process.SafeHandle;
            return process;
        }
        catch { process.Dispose(); throw; }
    }
    private Process Attached => portable ?? (attached ??= AttachProcess());
    internal bool WaitForExit(int milliseconds)
    {
        if (portable is not null) return portable.WaitForExit(milliseconds);
        if (milliseconds < -1) throw new ArgumentOutOfRangeException(nameof(milliseconds));
        var result = WaitForSingleObject(SafeHandle, unchecked((uint)milliseconds));
        if (result == 0xFFFFFFFF) throw new Win32Exception(Marshal.GetLastWin32Error());
        return result == 0;
    }
    internal bool WaitForExit(TimeSpan timeout) => WaitForExit(checked((int)timeout.TotalMilliseconds));
    internal void WaitForExit() => WaitForExit(-1);
    internal async Task WaitForExitAsync(CancellationToken cancellationToken = default)
    {
        if (portable is not null) await portable.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        else
        {
            var retained = false;
            SafeHandle.DangerousAddRef(ref retained);
            try
            {
                using var wait = new ExitWaitHandle(SafeHandle);
                var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var registration = ThreadPool.RegisterWaitForSingleObject(wait, (_, _) => exited.TrySetResult(),
                    null, Timeout.Infinite, executeOnlyOnce: true);
                try { await exited.Task.WaitAsync(cancellationToken).ConfigureAwait(false); }
                finally { registration.Unregister(null); }
            }
            finally { if (retained) SafeHandle.DangerousRelease(); }
        }
        await Task.WhenAll(outputLines ?? Task.CompletedTask, errorLines ?? Task.CompletedTask)
            .WaitAsync(cancellationToken).ConfigureAwait(false);
    }
    private sealed class ExitWaitHandle : WaitHandle
    {
        internal ExitWaitHandle(SafeProcessHandle process) => SafeWaitHandle = new SafeWaitHandle(process.DangerousGetHandle(), ownsHandle: false);
    }
    internal void BeginOutputReadLine() => outputLines = ReadLines(StandardOutput, line => OutputDataReceived?.Invoke(this, new(line)));
    internal void BeginErrorReadLine() => errorLines = ReadLines(StandardError, line => ErrorDataReceived?.Invoke(this, new(line)));
    private static async Task ReadLines(StreamReader reader, Action<string?> notify)
    {
        try
        {
            string? line;
            while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) is not null) notify(line);
            notify(null);
        }
        catch (ObjectDisposedException) { } // Exit cleanup can close startup diagnostic streams.
        catch (IOException) { }
    }
    internal void Kill(bool entireProcessTree = false)
    {
        if (portable is not null) { portable.Kill(entireProcessTree); return; }
        if (HasExited) return;
        if (entireProcessTree)
        {
            try { Attached.Kill(entireProcessTree: true); }
            catch (ArgumentException) when (HasExited)
            {
                // The process can exit between the native check and GetProcessById.
                // The retained native handle proves that this exact process exited.
            }
        }
        else if (!TerminateProcess(SafeHandle, uint.MaxValue))
        {
            var failure = new Win32Exception(Marshal.GetLastWin32Error());
            // Match Process.Kill: an exit code can be available before the process
            // handle signals. Only forgive access denied with proof of termination.
            if (failure.NativeErrorCode == 5 &&
                GetExitCodeProcess(SafeHandle, out var exitCode) && exitCode != 259) return;
            throw failure;
        }
    }
    public void Dispose()
    {
        input?.Dispose(); output?.Dispose(); error?.Dispose();
        attached?.Dispose(); portable?.Dispose(); handle?.Dispose();
    }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetExitCodeProcess(SafeProcessHandle handle, out uint code);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetProcessTimes(SafeProcessHandle handle, out long creation, out long exit, out long kernel, out long user);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool TerminateProcess(SafeProcessHandle handle, uint code);
    [DllImport("kernel32.dll")] private static extern uint GetConsoleCP();
    [DllImport("kernel32.dll")] private static extern uint GetConsoleOutputCP();
    [DllImport("kernel32.dll")] private static extern uint GetACP();
}
