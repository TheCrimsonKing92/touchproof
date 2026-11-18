using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        using var release = Environment.GetEnvironmentVariable("NATIVE_ARGUMENT_PROBE_RELEASE") is { } name
            ? EventWaitHandle.OpenExisting(name) : null;
        var consolePids = new uint[256];
        var count = GetConsoleProcessList(consolePids, (uint)consolePids.Length);
        if (count > consolePids.Length)
        {
            consolePids = new uint[count];
            count = GetConsoleProcessList(consolePids, (uint)consolePids.Length);
        }
        if (count > consolePids.Length)
            throw new InvalidOperationException("Probe console membership exceeded its buffer.");
        File.WriteAllText(Required("RECORD"), JsonSerializer.Serialize(new
        {
            Args = args,
            WorkingDirectory = Environment.CurrentDirectory,
            Pid = Environment.ProcessId,
            ConsolePids = consolePids.Take((int)count).ToArray()
        }));

        using var stdout = Console.OpenStandardOutput();
        using var stderr = Console.OpenStandardError();
        await Task.WhenAll(WriteBytes(stdout, "STDOUT"), WriteBytes(stderr, "STDERR"));
        if (release is not null && !release.WaitOne(TimeSpan.FromSeconds(60)))
            throw new TimeoutException("Probe release event was never signaled.");
        await Task.WhenAll(WriteBytes(stdout, "STDOUT_TAIL"), WriteBytes(stderr, "STDERR_TAIL"));
        return int.Parse(Required("EXIT_CODE"), CultureInfo.InvariantCulture);
    }

    private static string Required(string suffix) =>
        Environment.GetEnvironmentVariable("NATIVE_ARGUMENT_PROBE_" + suffix)
        ?? throw new InvalidOperationException("Missing probe setting: " + suffix);

    private static async Task WriteBytes(Stream stream, string suffix)
    {
        await stream.WriteAsync(Convert.FromBase64String(Required(suffix)));
        await stream.FlushAsync();
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetConsoleProcessList([Out] uint[] processList, uint processCount);
}
