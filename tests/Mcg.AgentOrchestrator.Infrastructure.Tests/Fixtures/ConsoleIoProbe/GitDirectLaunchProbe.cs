using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Win32.SafeHandles;

internal static class GitDirectLaunchProbe
{
    internal static int Run(string[] args)
    {
        var reportPath = args[0];
        var directory = Path.GetDirectoryName(reportPath)!;
        try
        {
            var shim = HandleListProcessLauncher.ResolveApplication("git");
            var cacheCreatedBefore = GitExecutable.IsInstallationCreated;
            // No kill-on-close limit: this job contains the measurement host itself.
            using var job = Windows.CreateJobObjectW(IntPtr.Zero, null);
            if (job.IsInvalid || !Windows.AssignProcessToJobObject(job, Windows.GetCurrentProcess()))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Measurement host job assignment failed.");
            var consoleWindow = Windows.GetConsoleWindow().ToInt64();
            var consoleProcessCount = Windows.GetConsoleProcessList(new uint[1], 1);
            var before = Total(job);
            string? image = null;
            uint flags = 0;
            void Observe(HandleListLaunchedProcess process)
            {
                image = Image(process);
                flags = process.EffectiveCreationFlags;
            }

            GitCli.GitResult result;
            string[] execMarkers = [], hookMarkers = [];
            bool rebaseDirectoryExists = false;
            if (args[1] == "shell")
            {
                Git(directory, "init", "-q");
                Git(directory, "config", "user.name", "Direct launch test");
                Git(directory, "config", "user.email", "direct-launch@example.invalid");
                Git(directory, "config", "commit.gpgSign", "false");
                Git(directory, "config", "core.autocrlf", "false");
                var hooks = Path.Combine(directory, ".git", "direct-hooks");
                Directory.CreateDirectory(hooks);
                Git(directory, "config", "core.hooksPath", hooks.Replace('\\', '/'));
                for (var index = 0; index < 3; index++)
                {
                    File.WriteAllText(Path.Combine(directory, "tracked.txt"), $"commit {index}\n");
                    Git(directory, "add", "tracked.txt");
                    Git(directory, "commit", "-qm", $"commit {index}");
                }
                // The hook explicitly finds a second shell on PATH, not just its shebang interpreter.
                File.WriteAllText(Path.Combine(hooks, "post-rewrite"),
                    "#!/bin/sh\nsh -c 'count=0; while read old new rest; do count=$((count+1)); done; " +
                    "printf \"hook:%s\\n\" \"$count\" > .git/hook-marker'\n", new UTF8Encoding(false));
                result = GitCli.RunExecutable("git", directory, 120_000,
                    ["rebase", "--no-ff", "--exec", "sh -c 'printf \"exec:%s\\n\" \"$MSYSTEM\" >> .git/exec-marker'", "HEAD~2"],
                    null, null, Observe);
                execMarkers = ReadMarkers(directory, "exec-marker");
                hookMarkers = ReadMarkers(directory, "hook-marker");
                rebaseDirectoryExists = Directory.Exists(Path.Combine(directory, ".git", "rebase-merge"));
            }
            else
            {
                result = GitCli.RunExecutable(args[1] == "shim-version" ? shim : "git",
                    directory, 120_000, ["--version"], null, null, Observe);
            }

            File.WriteAllText(reportPath, JsonSerializer.Serialize(new
            {
                error = (string?)null, shim, consoleWindow, consoleProcessCount,
                cacheCreatedBefore, cacheCreatedAfter = GitExecutable.IsInstallationCreated,
                before, after = Total(job), image, flags, result.ExitCode, result.Output, gitError = result.Error,
                result.DrainTimedOut, result.ProcessStarted,
                execMarkers, hookMarkers, rebaseDirectoryExists
            }));
            return result.Succeeded ? 0 : 1;
        }
        catch (Exception exception)
        {
            File.WriteAllText(reportPath, JsonSerializer.Serialize(new { error = exception.ToString() }));
            return 1;
        }
    }

    private static void Git(string directory, params string[] args)
    {
        var result = GitCli.Run(directory, 120_000, args);
        if (!result.Succeeded || result.DrainTimedOut)
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {result.ExitCode} {result.Error}");
    }

    private static string[] ReadMarkers(string directory, string name) =>
        File.Exists(Path.Combine(directory, ".git", name)) ? File.ReadAllLines(Path.Combine(directory, ".git", name)) : [];

    private static uint Total(SafeFileHandle job)
    {
        if (!Windows.QueryInformationJobObject(job, 1, out var accounting,
                Marshal.SizeOf<Windows.Accounting>(), IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Measurement job accounting failed.");
        return accounting.TotalProcesses;
    }

    private static string Image(HandleListLaunchedProcess process)
    {
        var buffer = new StringBuilder(32768);
        var length = (uint)buffer.Capacity;
        if (!Windows.QueryFullProcessImageNameW(process.Handle, 0, buffer, ref length))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Launched git image query failed.");
        return buffer.ToString();
    }

    private static class Windows
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern SafeFileHandle CreateJobObjectW(IntPtr attributes, string? name);
        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);
        [DllImport("kernel32.dll")] internal static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll")] internal static extern IntPtr GetConsoleWindow();
        [DllImport("kernel32.dll")] internal static extern uint GetConsoleProcessList([Out] uint[] ids, uint count);
        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool QueryInformationJobObject(SafeFileHandle job, int informationClass,
            out Accounting accounting, int size, IntPtr returnedLength);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern bool QueryFullProcessImageNameW(IntPtr process, uint flags, StringBuilder path, ref uint capacity);

        [StructLayout(LayoutKind.Sequential)]
        internal struct Accounting
        {
            internal long TotalUserTime, TotalKernelTime, ThisPeriodUserTime, ThisPeriodKernelTime;
            internal uint TotalPageFaultCount, TotalProcesses, ActiveProcesses, TotalTerminatedProcesses;
        }
    }
}
