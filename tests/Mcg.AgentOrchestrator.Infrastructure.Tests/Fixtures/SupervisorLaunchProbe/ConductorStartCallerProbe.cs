using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Microsoft.Win32.SafeHandles;

internal static class ConductorStartCallerProbe
{
    internal static int Run(string root, string token)
    {
        // Own a console even when the test host has none. Preserve the merged output
        // pipe because AllocConsole may initialize the process standard handles.
        var streams = new[] { -10, -11, -12 };
        var handles = streams.Select(GetStdHandle).ToArray();
        if (GetConsoleProcessList(new uint[1], 1) == 0)
        {
            if (!AllocConsole()) throw new Win32Exception(Marshal.GetLastWin32Error());
            for (var index = 0; index < streams.Length; index++)
                if (!SetStdHandle(streams[index], handles[index]))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        var consoleProcesses = new uint[64];
        var consoleCount = GetConsoleProcessList(consoleProcesses, (uint)consoleProcesses.Length);
        if (consoleCount == 0 || consoleCount > consoleProcesses.Length)
            throw new InvalidOperationException("Caller must have a test-owned console.");
        File.WriteAllText(Path.Combine(root, "caller-console.json"), JsonSerializer.Serialize(new
        {
            pid = Environment.ProcessId,
            processIds = consoleProcesses.Take((int)consoleCount)
        }));
        // Prove the fixture actually supplies inheritable caller pipe handles.
        foreach (var stream in new[] { -11, -12 })
        {
            var handle = GetStdHandle(stream);
            if (GetFileType(handle) != 3) throw new InvalidOperationException("Caller output must be a pipe.");
            if (!SetHandleInformation(handle, 1, 1)) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        return CliConductorCommand.Run(["conductor", "start"], OrchestratorWorkspace.ForDirectory(root),
            launcher: new FakeCommandLauncher(root, token), jobProbe: ProcessJobTrapProbe.UnknownProbe);
    }

    private sealed class FakeCommandLauncher(string root, string token) : IConductorProcessLauncher
    {
        public ConductorLaunchResult Launch(ConductorLaunchRequest request)
        {
            var result = new HandleListConductorProcessLauncher().Launch(request with
            {
                FileName = Environment.ProcessPath!,
                Arguments = [typeof(ConductorStartCallerProbe).Assembly.Location, "fake-conductor", token, "60", root]
            });
            File.WriteAllText(Path.Combine(root, "launch-receipt.json"), result.StandardOutput ?? "{}");
            if (result.ExitCode == 0)
            {
                CaptureIdentity(root, result.StandardOutput);
                // Keep the actual launching process and its console alive while the
                // fake captures membership; caller exit must not hide inheritance.
                using var ready = EventWaitHandle.OpenExisting(token);
                if (!ready.WaitOne(TimeSpan.FromMinutes(1)))
                    throw new TimeoutException("Fake conductor did not capture its console and flush its logs.");
            }
            return result;
        }
    }

    internal static void CaptureIdentity(string root, string? receiptText)
    {
        int? pid = null;
        int? openWin32Error = null;
        SafeProcessHandle? handle = null;
        ProcessImagePathResolver.Result? image = null;
        var stage = "parse-receipt";
        try
        {
            using var receipt = JsonDocument.Parse(receiptText!);
            pid = receipt.RootElement.GetProperty("pid").GetInt32();
            stage = "open-process";
            handle = ProcessImagePathResolver.Open(pid.Value, out var openError);
            openWin32Error = openError;
            if (handle.IsInvalid) throw new Win32Exception(openError);
            stage = "resolve-image";
            image = ProcessImagePathResolver.Resolve(handle);
            if (image.Status != ProcessImagePathResolver.Status.Resolved)
                throw new InvalidOperationException($"Process image query: {image.Status}; exit code: {image.ExitCode}; Win32 error: {image.Win32Error}.");
            stage = "read-start-time";
            using var process = Process.GetProcessById(pid.Value);
            var startedAt = process.StartTime.ToUniversalTime();
            stage = "write-identity";
            File.WriteAllText(Path.Combine(root, "launch-identity.json"), JsonSerializer.Serialize(new
            {
                pid = pid.Value,
                startedAt,
                imagePath = image.Path
            }));
        }
        catch (Exception failure)
        {
            try
            {
                var state = handle is { IsInvalid: false, IsClosed: false }
                    ? ProcessImagePathResolver.ReadState(handle) : null;
                File.WriteAllText(Path.Combine(root, "launch-identity-error.json"), JsonSerializer.Serialize(new
                {
                    exceptionType = failure.GetType().FullName,
                    message = failure.Message,
                    stage,
                    pid,
                    pidUnavailable = pid is null ? "unavailable: receipt pid not parsed" : null,
                    exited = state?.Exited,
                    exitCode = state?.ExitCode,
                    stateUnavailable = state?.Exited is null ? "unavailable: process handle could not be inspected" : null,
                    exitCodeUnavailable = state?.Exited == false ? "unavailable: process still running" :
                        state?.ExitCode is null ? "unavailable: process state unknown" : null,
                    openWin32Error,
                    openUnavailable = openWin32Error is null ? "unavailable: opening not attempted" : null,
                    queryWin32Error = image?.Win32Error,
                    stateWin32Error = state?.Win32Error
                }));
            }
            catch (Exception diagnosticFailure)
            {
                // A diagnostic-write failure must not replace the original cause.
                try { Console.Error.WriteLine("Identity diagnostic could not be written: " + diagnosticFailure); }
                catch (Exception) { /* Preserve the capture failure even if the caller pipe is broken. */ }
            }
            throw;
        }
        finally { handle?.Dispose(); }
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetStdHandle(int stream);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllocConsole();
    [DllImport("kernel32.dll")]
    private static extern uint GetConsoleProcessList([Out] uint[] processes, uint count);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetStdHandle(int stream, IntPtr handle);
    [DllImport("kernel32.dll")]
    private static extern uint GetFileType(IntPtr handle);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetHandleInformation(IntPtr handle, uint mask, uint flags);
}
