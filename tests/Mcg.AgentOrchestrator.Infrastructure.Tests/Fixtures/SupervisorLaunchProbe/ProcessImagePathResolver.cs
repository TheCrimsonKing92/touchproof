using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

// Borrows a caller-owned handle. Kernel image metadata is available even before
// the initial thread runs; module enumeration depends on the child's loader.
internal static class ProcessImagePathResolver
{
    internal enum Status { Resolved, Exited, Inaccessible, QueryFailed }
    internal sealed record Result(Status Status, string? Path, int? ExitCode, int Win32Error);
    internal sealed record State(bool? Exited, int? ExitCode, int Win32Error);

    internal static SafeProcessHandle Open(int pid, out int win32Error)
    {
        const uint queryLimitedInformationAndSynchronize = 0x1000 | 0x00100000;
        var handle = OpenProcess(queryLimitedInformationAndSynchronize, false, pid);
        win32Error = handle.IsInvalid ? Marshal.GetLastPInvokeError() : 0;
        return handle;
    }

    internal static Result Resolve(SafeProcessHandle handle)
    {
        var before = ReadState(handle);
        if (before.Exited != false) return FromState(before);
        try
        {
            var capacity = 32768u;
            var path = new StringBuilder((int)capacity);
            var queried = QueryFullProcessImageName(handle, 0, path, ref capacity);
            var queryError = queried ? 0 : Marshal.GetLastPInvokeError();
            // A failed image query may be the result of an intervening exit.
            var after = ReadState(handle);
            if (after.Exited != false) return FromState(after);
            return queried
                ? new(Status.Resolved, path.ToString(), null, 0)
                : Failure(queryError);
        }
        catch (ObjectDisposedException) { return Failure(6); }
    }

    internal static State ReadState(SafeProcessHandle handle)
    {
        if (handle.IsClosed || handle.IsInvalid) return new(null, null, 6);
        try
        {
            var wait = WaitForSingleObject(handle, 0);
            if (wait == uint.MaxValue) return new(null, null, Marshal.GetLastPInvokeError());
            if (!GetExitCodeProcess(handle, out var exitCode))
                return new(null, null, Marshal.GetLastPInvokeError());
            // The signaled handle distinguishes an exit code of STILL_ACTIVE
            // (259) from a process that is actually still running.
            return wait == 0 || exitCode != 259
                ? new(true, unchecked((int)exitCode), 0)
                : new(false, null, 0);
        }
        catch (ObjectDisposedException) { return new(null, null, 6); }
    }

    private static Result FromState(State state) => state.Exited == true
        ? new(Status.Exited, null, state.ExitCode, 0)
        : Failure(state.Win32Error);

    private static Result Failure(int error) =>
        new(error == 5 ? Status.Inaccessible : Status.QueryFailed, null, null, error);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int processId);

    [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", ExactSpelling = true,
        CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(SafeProcessHandle process,
        uint flags, StringBuilder path, ref uint capacity);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeProcess(SafeProcessHandle process, out uint exitCode);
}
