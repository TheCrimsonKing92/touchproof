using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each test owns its directory, registry, child job and capture pipes.
// Process observations are restricted to the suspended root supplied by that launch.
public sealed class AcceptanceDirectCheckLaunchTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ResolvedNativeCheckPreservesEveryArgument(bool ownedCapture)
    {
        if (!OperatingSystem.IsWindows()) return;
        var directory = SharedTestSupport.CreateTempDirectory();
        try
        {
            var image = Path.Combine(directory, "probe.exe");
            File.WriteAllBytes(image, []); // Resolution only; this test never launches the image.
            string[] values = ["a \"b\" & ^c %PATH%", "", @"trailing space \", "plain"];
            string[] arguments = ["probe", .. values];
            var info = AcceptanceShellProcessStartInfo.Build(arguments, directory,
                ownedCapture ? @"\\.\pipe\direct-out" : null,
                ownedCapture ? @"\\.\pipe\direct-err" : null,
                false, environment =>
                {
                    environment["PATH"] = directory;
                    environment["PATHEXT"] = ".exe";
                }, true, out var resolved);

            Assert.Equal(image, resolved);
            Assert.Equal(image, info.FileName);
            Assert.Empty(info.Arguments);
            Assert.Equal(values, info.ArgumentList);
            Assert.Equal((string[])["probe", .. values], arguments);
            Assert.False(info.UseShellExecute);
            Assert.Equal(!ownedCapture, info.RedirectStandardOutput);
            Assert.Equal(!ownedCapture, info.RedirectStandardError);
        }
        finally { SharedTestSupport.RemoveTempDirectory(directory); }
    }

    [Theory]
    [InlineData("probe.exe")]
    [InlineData("probe.com")]
    public void ExtendedNativeNameUsesChildPathWithoutChangingResolutionReceipt(string command)
    {
        if (!OperatingSystem.IsWindows()) return;
        var directory = SharedTestSupport.CreateTempDirectory();
        try
        {
            var image = Path.Combine(directory, command);
            File.WriteAllBytes(image, []);
            var info = AcceptanceShellProcessStartInfo.Build([command, "value"], Path.GetTempPath(),
                null, null, false, environment => environment["PATH"] = directory,
                true, out var resolved);

            Assert.Null(resolved); // The public bare-name resolver leaves extended names alone.
            Assert.Equal(image, info.FileName);
            Assert.Empty(info.Arguments);
            Assert.Equal(new[] { "value" }, info.ArgumentList);
        }
        finally { SharedTestSupport.RemoveTempDirectory(directory); }
    }

    [Fact]
    public void ForcedUtf8CheckKeepsCmdCodePagePreflightAndOwnedRedirection()
    {
        if (!OperatingSystem.IsWindows()) return;
        var image = Path.Combine(Environment.SystemDirectory, "whoami.exe");
        const string stdout = @"\\.\pipe\utf8-out";
        const string stderr = @"\\.\pipe\utf8-err";
        var info = AcceptanceShellProcessStartInfo.Build([image, "value"], Path.GetTempPath(),
            stdout, stderr, true, null, false, out _);

        Assert.Equal("cmd.exe", info.FileName);
        Assert.Empty(info.ArgumentList);
        Assert.Equal($"/c \"(chcp 65001 > nul && \"{image}\" \"value\") > \"{stdout}\" 2> \"{stderr}\"\"",
            info.Arguments);
        Assert.False(info.RedirectStandardOutput);
        Assert.False(info.RedirectStandardError);
    }

    [Fact(Timeout = TestHangGuard.WholeTestMilliseconds)]
    public async Task OrdinaryChildCapturesExactBytesAndExitSevenWithoutCmd()
    {
        if (!OperatingSystem.IsWindows()) return;
        var directory = SharedTestSupport.CreateTempDirectory();
        GoalAcceptanceVerifier.CommandResult? result = null;
        try
        {
            byte[] stdout = [0, 65, 128, 255, 13, 10];
            byte[] stderr = [66, 0, 129, 254];
            var script = $"$o=[Convert]::FromBase64String('{Convert.ToBase64String(stdout)}'); " +
                $"$e=[Convert]::FromBase64String('{Convert.ToBase64String(stderr)}'); " +
                "[Console]::OpenStandardOutput().Write($o,0,$o.Length); " +
                "[Console]::OpenStandardError().Write($e,0,$e.Length); exit 7";
            var run = await RunObservedChildAsync(script, directory);
            result = run.Result;

            Assert.Equal(7, result.ExitCode);
            Assert.False(result.TimedOut);
            Assert.False(result.CaptureLimited);
            Assert.Equal("powershell.exe", Path.GetFileName(run.Image), ignoreCase: true);
            Assert.Equal(run.ProcessId, result.ChildProcessId);
            Assert.Equal(run.ProcessId, run.CommandProcessId);
            Assert.Equal(stdout, await File.ReadAllBytesAsync(result.StdoutPath!));
            Assert.Equal(stderr, await File.ReadAllBytesAsync(result.StderrPath!));
            Assert.Equal(stdout.LongLength, result.StdoutBytes);
            Assert.Equal(stderr.LongLength, result.StderrBytes);
        }
        finally
        {
            DeleteCaptures(result);
            SharedTestSupport.RemoveTempDirectory(directory);
        }
    }

    [Fact(Timeout = TestHangGuard.WholeTestMilliseconds)]
    public async Task ArgumentBeyondCmdLimitArrivesIntactAtDirectChild()
    {
        if (!OperatingSystem.IsWindows()) return;
        var directory = SharedTestSupport.CreateTempDirectory();
        GoalAcceptanceVerifier.CommandResult? result = null;
        try
        {
            var payload = new string('x', 20000);
            var script = $"[Console]::Out.Write('{payload}'.Length)";
            Assert.InRange(script.Length, 8192, 32766);
            var run = await RunObservedChildAsync(script, directory);
            result = run.Result;

            Assert.Equal("powershell.exe", Path.GetFileName(run.Image), ignoreCase: true);
            Assert.Equal(0, result.ExitCode);
            Assert.False(result.TimedOut);
            Assert.False(result.CaptureLimited);
            Assert.Equal("20000", result.StandardOutput);
            Assert.Empty(result.Stderr!);
            Assert.Equal(Encoding.ASCII.GetBytes("20000"), await File.ReadAllBytesAsync(result.StdoutPath!));
            Assert.Empty(await File.ReadAllBytesAsync(result.StderrPath!));
        }
        finally
        {
            DeleteCaptures(result);
            SharedTestSupport.RemoveTempDirectory(directory);
        }
    }

    private static async Task<ChildRun> RunObservedChildAsync(string script, string directory)
    {
        var registryPath = Path.Combine(directory, "spawn-registry.db");
        _ = StateDbMigrations.EnsureUpToDate(registryPath);
        using var registry = WorkerProcessJobs.UseRegistryScopeForTests(registryPath);
        using var guard = new CancellationTokenSource(TestHangGuard.HangSafetyBound);
        var processId = 0;
        var commandProcessId = 0;
        string? image = null;
        var powershell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        var result = await AcceptanceProcessRunner.RunProcessForTestsAsync(
            [powershell, "-NoProfile", "-NonInteractive", "-Command", script], directory,
            TimeSpan.FromMinutes(5), cancellationToken: guard.Token, keepCaptureFiles: true,
            commandIdentityObserver: identity => commandProcessId = identity.ProcessId,
            resumeObserver: id =>
            {
                processId = id;
                using var process = Process.GetProcessById(id);
                var capacity = 32768u;
                var path = new StringBuilder((int)capacity);
                if (!QueryFullProcessImageNameW(process.Handle, 0, path, ref capacity))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                image = path.ToString();
            });
        Assert.NotNull(image); // The callback must observe the suspended root before it can exit.
        Assert.Equal(powershell, image, ignoreCase: true);
        return new ChildRun(result, image, processId, commandProcessId);
    }

    private static void DeleteCaptures(GoalAcceptanceVerifier.CommandResult? result)
    {
        if (result?.StdoutPath is { } stdout) File.Delete(stdout);
        if (result?.StderrPath is { } stderr) File.Delete(stderr);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageNameW(IntPtr process, uint flags, StringBuilder path, ref uint size);

    private sealed record ChildRun(GoalAcceptanceVerifier.CommandResult Result, string Image,
        int ProcessId, int CommandProcessId);
}
