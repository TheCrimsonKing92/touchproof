using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;

internal static class FakeConductorProbe
{
    internal static int Run(string token, string lifetimeSeconds, string root)
    {
        using var ready = EventWaitHandle.OpenExisting(token);
        using var release = EventWaitHandle.OpenExisting(token + "-release");
        var consoleProcesses = new uint[64];
        var consoleCount = GetConsoleProcessList(consoleProcesses, (uint)consoleProcesses.Length);
        if (consoleCount == 0)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Fake conductor console membership could not be read.");
        if (consoleCount > consoleProcesses.Length)
            throw new InvalidOperationException("Fake conductor console process list is incomplete.");
        File.WriteAllText(Path.Combine(root, "conductor-console.json"), JsonSerializer.Serialize(new
        {
            pid = Environment.ProcessId,
            processIds = consoleProcesses.Take((int)consoleCount)
        }));
        Console.Out.WriteLine($"fake-conductor-stdout pid={Environment.ProcessId}");
        Console.Error.WriteLine($"fake-conductor-stderr pid={Environment.ProcessId}");
        Console.Out.Flush();
        Console.Error.Flush();
        ready.Set();
        // Hold the owned process at an explicit lifecycle boundary. The timeout
        // only detects an absent fixture signal; it never selects test success.
        if (!release.WaitOne(TimeSpan.FromSeconds(int.Parse(lifetimeSeconds, CultureInfo.InvariantCulture))))
            throw new TimeoutException("Fake conductor release signal absent.");
        return 0;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetConsoleProcessList([Out] uint[] processes, uint count);
}
