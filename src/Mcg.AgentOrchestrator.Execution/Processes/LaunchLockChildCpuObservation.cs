using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Mcg.AgentOrchestrator.Infrastructure;

// The duplicate is not inheritable and survives disposal of the launcher's process object.
internal sealed class LaunchLockChildCpuObservation : WaitHandle
{
    private readonly LaunchLockChildCpuTracker.Token token;
    private readonly Action<LaunchLockChildCpuObservation> completed;
    private RegisteredWaitHandle? registration;

    internal LaunchLockChildCpuObservation(SafeHandle process, LaunchLockChildCpuTracker.Token token,
        Action<LaunchLockChildCpuObservation> completed)
    {
        this.token = token;
        this.completed = completed;
        if (!DuplicateHandle(new IntPtr(-1), process, new IntPtr(-1), out var duplicate, 0, false, 2))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        SafeWaitHandle = duplicate;
    }

    internal void Register() => registration = ThreadPool.UnsafeRegisterWaitForSingleObject(this,
        static (state, _) => { var observation = (LaunchLockChildCpuObservation)state!; observation.completed(observation); },
        this, Timeout.Infinite, executeOnlyOnce: true);

    internal bool HasExited => WaitOne(0);

    internal void Credit()
    {
        if (GetProcessTimes(SafeWaitHandle, out _, out _, out var kernel, out var user))
            token.Exited(TimeSpan.FromTicks(kernel + user));
        else { token.Exited(null); LaunchLockChildCpuTracker.ReportFailure(); }
    }

    protected override void Dispose(bool explicitDisposing)
    {
        registration?.Unregister(null);
        base.Dispose(explicitDisposing);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DuplicateHandle(IntPtr sourceProcess, SafeHandle source, IntPtr targetProcess,
        out SafeWaitHandle target, uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint options);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(SafeWaitHandle process, out long creation, out long exit,
        out long kernel, out long user);
}
