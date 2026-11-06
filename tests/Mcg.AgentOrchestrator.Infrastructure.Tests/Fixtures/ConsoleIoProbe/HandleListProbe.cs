using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

internal static class HandleListProbe
{
    internal static int Run(string[] args)
    {
        switch (args[0])
        {
            case "handles":
                Console.WriteLine(JsonSerializer.Serialize(Handles()));
                Console.Out.Flush();
                using (var release = EventWaitHandle.OpenExisting(args[1]))
                    if (!release.WaitOne(TimeSpan.FromMinutes(10))) throw new TimeoutException("Handle probe release was not signaled.");
                return 0;
            case "streams":
                // Interleaving exceeds both anonymous-pipe buffers; sequential drains deadlock.
                for (var i = 0; i < 1024; i++)
                {
                    Console.Out.Write(new string('o', 1024) + "é\n");
                    Console.Error.Write(new string('e', 1024) + "λ\n");
                }
                return 0;
            case "exit": Console.Error.Write("probe-error"); return 3;
            case "echo": Console.Write(JsonSerializer.Serialize(args[1..])); return 0;
            case "tree":
                var info = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
                info.ArgumentList.Add("--handle-list-probe"); info.ArgumentList.Add("wait"); info.ArgumentList.Add(args[1]);
                using (var child = Mcg.AgentOrchestrator.Infrastructure.HandleListProcessLauncher.Start(info))
                {
                    File.WriteAllText(args[2], child.Id.ToString());
                    using var ready = EventWaitHandle.OpenExisting(args[3]);
                    ready.Set();
                    using var release = EventWaitHandle.OpenExisting(args[1]);
                    if (!release.WaitOne(TimeSpan.FromMinutes(10))) throw new TimeoutException("Tree probe release was not signaled.");
                }
                return 0;
            case "wait":
                using (var release = EventWaitHandle.OpenExisting(args[1]))
                    if (!release.WaitOne(TimeSpan.FromMinutes(10))) throw new TimeoutException("Tree child release was not signaled.");
                return 0;
            default: throw new ArgumentException("Unknown handle-list probe mode.");
        }
    }

    private static long[] Handles()
    {
        for (var size = 65536; size <= 16 * 1024 * 1024; size *= 2)
        {
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                var status = NtQueryInformationProcess(new IntPtr(-1), 51, buffer, size, out _);
                if (status == unchecked((int)0xC0000004) || status == unchecked((int)0xC0000023)) continue;
                if (status != 0) throw new Win32Exception($"Handle snapshot failed: {status:X8}");
                var count = checked((int)Marshal.ReadIntPtr(buffer).ToInt64());
                var stride = IntPtr.Size * 3 + 16;
                return Enumerable.Range(0, count).Select(i => Marshal.ReadIntPtr(buffer, IntPtr.Size * 2 + i * stride).ToInt64()).ToArray();
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        throw new InvalidOperationException("Handle snapshot exceeded its bounded buffer.");
    }
    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(IntPtr process, int information, IntPtr buffer, int size, out int required);
}
