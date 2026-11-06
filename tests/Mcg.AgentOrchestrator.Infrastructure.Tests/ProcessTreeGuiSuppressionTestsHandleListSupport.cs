using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Mcg.AgentOrchestrator.Infrastructure;

internal static class ProcessTreeGuiSuppressionTestsHandleListSupport
{
    internal static ProcessStartInfo Probe(string root, params string[] args)
    {
        var info = new ProcessStartInfo(ProcessTreeGuiSuppressionTestsProcessErrorMode.ProbeExecutable())
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = root,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        PinEnvironment(info, root);
        info.ArgumentList.Add("--handle-list-probe");
        foreach (var arg in args) info.ArgumentList.Add(arg);
        return info;
    }

    internal static void PinEnvironment(ProcessStartInfo info, string root)
    {
        ProcessTreeGuiSuppressionTestsProcessErrorMode.PinEnvironment(info, root);
        // A remote test host may use a private runtime with no machine registration.
        // Derive the apphost root from the loaded runtime, not ambient DOTNET_ROOT.
        var runtimeRoot = Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", ".."));
        Assert.True(File.Exists(Path.Combine(runtimeRoot, "dotnet.exe")),
            "The executing test runtime has no dotnet host: " + runtimeRoot);
        info.Environment["DOTNET_ROOT"] = runtimeRoot;
        info.Environment["DOTNET_ROOT_" + RuntimeInformation.ProcessArchitecture.ToString().ToUpperInvariant()] = runtimeRoot;
    }

    internal static SafeFileHandle Duplicate(IntPtr sourceProcess, IntPtr value)
    {
        if (!DuplicateHandle(sourceProcess, value, new IntPtr(-1), out var duplicate, 0, false, 2))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return duplicate;
    }
    internal static SafeFileHandle InheritableSentinel()
    {
        var attributes = new Attributes { Size = Marshal.SizeOf<Attributes>(), Inherit = true };
        var result = CreateEventW(ref attributes, true, false, null);
        if (result.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        return result;
    }
    internal static SafeProcessHandle OpenForDuplication(int pid)
    {
        var handle = OpenProcess(0x40, false, pid);
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        return handle;
    }
    internal static bool Same(SafeFileHandle a, SafeFileHandle b) => CompareObjectHandles(a, b);
    internal static void Stop(HandleListLaunchedProcess? child)
    {
        if (child is null) return;
        try { if (!child.HasExited) child.Kill(true); child.WaitForExit(60_000); }
        finally { child.Dispose(); }
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct Attributes { internal int Size; internal IntPtr Descriptor; [MarshalAs(UnmanagedType.Bool)] internal bool Inherit; }
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DuplicateHandle(IntPtr sourceProcess, IntPtr source, IntPtr targetProcess,
        out SafeFileHandle target, uint access, bool inherit, uint options);
    [DllImport("kernelbase.dll")] private static extern bool CompareObjectHandles(SafeFileHandle a, SafeFileHandle b);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateEventW(ref Attributes attributes, bool manual, bool initial, string? name);
}
