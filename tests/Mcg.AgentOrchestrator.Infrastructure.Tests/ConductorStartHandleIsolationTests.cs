using System.Diagnostics;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;
using Mcg.AgentOrchestrator.Infrastructure.Tests;

// Parallel-safe: unique roots, named events, child environments and process identities.
// Uses the prebuilt probe; no build leases or shared isolated dotnet root are needed.
public sealed class ConductorStartHandleIsolationTests
{
    public static bool IsWindows => OperatingSystem.IsWindows();

    [Fact(Skip = "Requires Windows console attachment.", SkipUnless = nameof(IsWindows))]
    public async Task Start_Conductor_DoesNotInheritCallerConsole()
    {
        using var fixture = new LaunchFixture();
        var output = fixture.CallerOutput;
        Assert.True(fixture.Caller.WaitForExit(60000), "CLI caller did not exit.");
        Assert.Equal(0, fixture.Caller.ExitCode);
        await output.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(fixture.Ready.WaitOne(TimeSpan.FromMinutes(1)), "Fake conductor did not report its console attachment.");
        using var callerConsole = JsonDocument.Parse(File.ReadAllText(Path.Combine(fixture.Root, "caller-console.json")));
        var callerPid = callerConsole.RootElement.GetProperty("pid").GetUInt32();
        Assert.Contains(callerPid, callerConsole.RootElement.GetProperty("processIds")
            .EnumerateArray().Select(value => value.GetUInt32()));
        using var conductor = fixture.OpenConductor();
        Assert.False(conductor.HasExited);
        using var conductorConsole = JsonDocument.Parse(File.ReadAllText(Path.Combine(fixture.Root, "conductor-console.json")));
        Assert.Equal(conductor.Id, conductorConsole.RootElement.GetProperty("pid").GetInt32());
        var conductorConsoleProcesses = conductorConsole.RootElement.GetProperty("processIds")
            .EnumerateArray().Select(value => value.GetUInt32()).ToArray();
        Assert.Contains((uint)conductor.Id, conductorConsoleProcesses);
        // CREATE_NO_WINDOW can provide a private windowless console. The caller
        // stays alive until this snapshot is written, so inheritance includes its pid.
        Assert.DoesNotContain(callerPid, conductorConsoleProcesses);
    }

    [Fact(Skip = "Requires Windows handle inheritance.", SkipUnless = nameof(IsWindows))]
    public async Task Start_MergedCallerPipe_EndsWhileConductorIsAlive()
    {
        using var fixture = new LaunchFixture();
        var output = fixture.CallerOutput;
        // WaitForExit with a bound observes process exit independently of pipe EOF.
        Assert.True(fixture.Caller.WaitForExit(60000), "CLI caller did not exit.");
        Assert.Equal(0, fixture.Caller.ExitCode);
        string text;
        try { text = await output.WaitAsync(TimeSpan.FromSeconds(10)); }
        catch (TimeoutException)
        {
            throw new TimeoutException("Merged caller pipe did not reach EOF after CLI exit.");
        }
        using var conductor = fixture.OpenConductor();
        Assert.False(conductor.HasExited);
        Assert.Contains($"Conductor launched: pid {conductor.Id}; stdout log: ", text);
        Assert.DoesNotContain("fake-conductor-stdout", text);
        Assert.DoesNotContain("fake-conductor-stderr", text);
    }

    [Fact(Skip = "Requires Windows handle inheritance.", SkipUnless = nameof(IsWindows))]
    public async Task Start_Receipt_IdentifiesConductorAndSeparateOutputLogs()
    {
        using var fixture = new LaunchFixture();
        var output = fixture.CallerOutput;
        Assert.True(fixture.Caller.WaitForExit(60000), "CLI caller did not exit.");
        Assert.Equal(0, fixture.Caller.ExitCode);
        var text = await output.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(fixture.Ready.WaitOne(TimeSpan.FromMinutes(1)), "Fake conductor did not flush both log markers.");
        using var conductor = fixture.OpenConductor();
        Assert.False(conductor.HasExited);
        using var receipt = JsonDocument.Parse(File.ReadAllText(Path.Combine(fixture.Root, "launch-receipt.json")));
        var value = receipt.RootElement;
        Assert.Equal(conductor.Id, value.GetProperty("pid").GetInt32());
        var stdoutPath = value.GetProperty("stdoutPath").GetString()!;
        var stderrPath = value.GetProperty("stderrPath").GetString()!;
        Assert.Equal($"Conductor launched: pid {conductor.Id}; stdout log: {stdoutPath}" + Environment.NewLine +
            "Check status: touchproof.cmd conductor status" + Environment.NewLine, text);
        Assert.Equal(Path.Combine(fixture.Root, ".orchestrator", "logs"), Path.GetDirectoryName(stdoutPath));
        Assert.Equal(Path.GetDirectoryName(stdoutPath), Path.GetDirectoryName(stderrPath));
        Assert.Matches(@"^operator-conduct-loop-daemon-\d{14}\.out\.log$", Path.GetFileName(stdoutPath));
        Assert.Equal(stdoutPath[..^8] + ".err.log", stderrPath);
        Assert.Equal($"fake-conductor-stdout pid={conductor.Id}" + Environment.NewLine, ReadLiveLog(stdoutPath));
        Assert.Equal($"fake-conductor-stderr pid={conductor.Id}" + Environment.NewLine, ReadLiveLog(stderrPath));
        Assert.Equal("60", value.GetProperty("args")[3].GetString());
    }

    [Fact(Skip = "Requires Windows process handles.", SkipUnless = nameof(IsWindows))]
    public async Task Resolve_LiveThenExitedChild_ReturnsImageThenExitCode()
    {
        var expectedPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
        var start = new ProcessStartInfo(expectedPath)
        {
            Arguments = "/d /q /c exit 23",
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        start.Environment.Clear();
        foreach (var key in new[] { "SystemRoot", "WINDIR", "TEMP", "TMP" })
            if (Environment.GetEnvironmentVariable(key) is { } value) start.Environment[key] = value;
        // The initial thread is the lifecycle gate: the child cannot exit or
        // initialize its loader until Resume. This forces the old missing-module
        // condition without a race, inherited output handles or pacing delays.
        using var child = OwnedProcessGroup.StartSuspended(start);
        using var handle = ProcessImagePathResolver.Open(child.Process.Id, out var openError);
        Assert.Equal(0, openError);
        Assert.False(handle.IsInvalid);
        for (var index = 0; index < 50; index++)
        {
            var live = ProcessImagePathResolver.Resolve(handle);
            Assert.Equal(ProcessImagePathResolver.Status.Resolved, live.Status);
            Assert.Equal(expectedPath, live.Path, ignoreCase: true);
            Assert.Null(live.ExitCode);
            Assert.Equal(0, live.Win32Error);
        }

        child.Resume();
        await child.WaitForOwnedExitAsync(TestContext.Current.CancellationToken);
        ProcessImagePathResolver.Result? exited = null;
        var failure = Record.Exception(() => exited = ProcessImagePathResolver.Resolve(handle));
        Assert.Null(failure);
        Assert.NotNull(exited);
        Assert.Equal(ProcessImagePathResolver.Status.Exited, exited.Status);
        Assert.Equal(23, exited.ExitCode);
        Assert.Null(exited.Path);
        Assert.Equal(0, exited.Win32Error);
    }

    [Theory(Skip = "Requires Windows process diagnostics.", SkipUnless = nameof(IsWindows))]
    [InlineData("parse-receipt")]
    [InlineData("open-process")]
    [InlineData("write-identity")]
    public async Task IdentityCapture_Failure_PublishesAndSurfacesDiagnostic(string stage)
    {
        // This caller creates no descendants. Its exit orders the fixture's pipe
        // drains; the actual capture method below supplies the failure artifact.
        using var fixture = new LaunchFixture(configureCallerStart: start => start.Arguments = "/d /q /c exit 23");
        try
        {
            await fixture.Caller.WaitForExitAsync(TestContext.Current.CancellationToken);
            Assert.Equal(23, fixture.Caller.ExitCode);
            var pid = stage == "open-process" ? 0 : Environment.ProcessId;
            var receipt = stage == "parse-receipt" ? "{}" : JsonSerializer.Serialize(new { pid });
            var identityPath = Path.Combine(fixture.Root, "launch-identity.json");
            if (stage == "write-identity") Directory.CreateDirectory(identityPath);
            var captureFailure = Record.Exception(() => ConductorStartCallerProbe.CaptureIdentity(fixture.Root, receipt));
            Assert.NotNull(captureFailure);
            if (stage == "parse-receipt") Assert.IsType<KeyNotFoundException>(captureFailure);
            else if (stage == "open-process") Assert.IsType<System.ComponentModel.Win32Exception>(captureFailure);
            else Assert.IsType<UnauthorizedAccessException>(captureFailure);
            if (stage == "write-identity") Directory.Delete(identityPath);
            Assert.False(File.Exists(identityPath));

            var diagnosticPath = Path.Combine(fixture.Root, "launch-identity-error.json");
            var diagnosticText = File.ReadAllText(diagnosticPath);
            using var diagnostic = JsonDocument.Parse(diagnosticText);
            var value = diagnostic.RootElement;
            Assert.Equal(captureFailure.GetType().FullName, value.GetProperty("exceptionType").GetString());
            Assert.Equal(captureFailure.Message, value.GetProperty("message").GetString());
            Assert.Equal(stage, value.GetProperty("stage").GetString());
            Assert.Equal(JsonValueKind.Null, value.GetProperty("exitCode").ValueKind);
            Assert.StartsWith("unavailable:", value.GetProperty("exitCodeUnavailable").GetString());
            if (stage == "parse-receipt")
            {
                Assert.Equal(JsonValueKind.Null, value.GetProperty("pid").ValueKind);
                Assert.StartsWith("unavailable:", value.GetProperty("pidUnavailable").GetString());
                Assert.Equal(JsonValueKind.Null, value.GetProperty("openWin32Error").ValueKind);
                Assert.Equal("unavailable: opening not attempted", value.GetProperty("openUnavailable").GetString());
            }
            else
            {
                Assert.Equal(pid, value.GetProperty("pid").GetInt32());
                Assert.Equal(stage == "open-process" ? 87 : 0, value.GetProperty("openWin32Error").GetInt32());
            }
            if (stage == "write-identity")
            {
                Assert.False(value.GetProperty("exited").GetBoolean());
                Assert.Equal(0, value.GetProperty("queryWin32Error").GetInt32());
                Assert.Equal(0, value.GetProperty("stateWin32Error").GetInt32());
            }
            else
            {
                Assert.Equal(JsonValueKind.Null, value.GetProperty("exited").ValueKind);
                Assert.StartsWith("unavailable:", value.GetProperty("stateUnavailable").GetString());
            }

            var cleanup = Assert.Throws<AggregateException>(fixture.Dispose);
            var surfaced = Assert.IsType<InvalidOperationException>(Assert.Single(cleanup.InnerExceptions));
            Assert.Contains(diagnosticPath, surfaced.Message);
            Assert.Contains(diagnosticText, surfaced.Message);
            Assert.Contains("Caller exit code: 23", surfaced.Message);
            Assert.Contains("Launch receipt (", surfaced.Message);
        }
        finally
        {
            // A missing identity intentionally retains the root. The controlled
            // caller has no children, so the test can remove its diagnostic files.
            _ = Record.Exception(fixture.Dispose);
            if (Directory.Exists(fixture.Root)) Directory.Delete(fixture.Root, recursive: true);
        }
    }

    private static string ReadLiveLog(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    internal sealed class LaunchFixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "mcg-start-pipe-" + Guid.NewGuid().ToString("N"));
        internal EventWaitHandle Ready { get; } = null!;
        internal EventWaitHandle Release { get; } = null!;
        internal EventWaitHandle ReleaseWatchdogExpired { get; } = null!;
        internal Process Caller { get; } = null!;
        internal Task<string> CallerOutput => callerOutput!;
        internal Task<string> CallerError => callerError!;
        private Task<string>? callerOutput;
        private Task<string>? callerError;
        private readonly Action<LaunchFixture>? beforeRootRemoval;
        private readonly Action<LaunchFixture, Process>? beforeConductorRelease;
        private bool disposed;

        // Diagnostic seams observe the real acquisition/removal boundaries. They
        // use the same cleanup path for rollback and normal disposal.
        internal LaunchFixture(Action<LaunchFixture>? afterCallerStarted = null,
            Action<LaunchFixture>? beforeRootRemoval = null,
            Action<LaunchFixture, Process>? beforeConductorRelease = null,
            Action<ProcessStartInfo>? configureCallerStart = null)
        {
            this.beforeRootRemoval = beforeRootRemoval;
            this.beforeConductorRelease = beforeConductorRelease;
            try
            {
                Directory.CreateDirectory(Root);
                var token = @"Local\mcg-start-pipe-" + Guid.NewGuid().ToString("N");
                Ready = new EventWaitHandle(false, EventResetMode.ManualReset, token);
                Release = new EventWaitHandle(false, EventResetMode.ManualReset, token + "-release");
                ReleaseWatchdogExpired = new EventWaitHandle(false, EventResetMode.ManualReset, token + "-release-watchdog");
                var location = DispatchProcessHostTestsConductorSupervisorWindowsLaunchAuthority.LocateProbe(GetType());
                Assert.NotNull(location.Directory);
                var probe = Path.Combine(location.Directory!, "Mcg.AgentOrchestrator.SupervisorLaunchProbe.dll");
                var command = ConductorLoopHandoff.ResolveWindowsNativeCommand(
                    ["dotnet", probe, "conductor-start-caller", Root, token], Root);
                var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"))
                {
                    Arguments = "/d /s /c \"" + ConductorLoopHandoff.BuildWindowsProcessCommandLine(command) + " 2>&1\"",
                    WorkingDirectory = Root,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    // AllocConsole in the caller probe honors the startup visibility.
                    WindowStyle = ProcessWindowStyle.Hidden,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                start.Environment.Clear();
                foreach (var key in new[] { "SystemRoot", "WINDIR", "PATH", "TEMP", "TMP", "USERPROFILE",
                             "LOCALAPPDATA", "DOTNET_ROOT", "DOTNET_ROOT_X64", "DOTNET_ROOT_X86" })
                    if (Environment.GetEnvironmentVariable(key) is { } value) start.Environment[key] = value;
                // Allow diagnostics to supply a caller that fails before publishing
                // ownership, while retaining the real process/pipe cleanup path.
                configureCallerStart?.Invoke(start);
                Caller = Process.Start(start) ?? throw new InvalidOperationException("Caller probe did not start.");
                _ = Caller.SafeHandle;
                callerOutput = Caller.StandardOutput.ReadToEndAsync();
                callerError = Caller.StandardError.ReadToEndAsync();
                Caller.StandardInput.Close();
                afterCallerStarted?.Invoke(this);
            }
            catch (Exception startupFailure)
            {
                try { Dispose(); }
                catch (Exception cleanupFailure)
                {
                    throw new AggregateException("Fixture startup and cleanup failed.", startupFailure, cleanupFailure);
                }
                throw;
            }
        }

        internal Process OpenConductor()
        {
            var identityPath = Path.Combine(Root, "launch-identity.json");
            string identityText;
            try { identityText = File.ReadAllText(identityPath); }
            catch (IOException failure) when (failure is FileNotFoundException or DirectoryNotFoundException)
            {
                // Caller exit already orders publication. A missing record cannot
                // become available by waiting; preserve the launch failure instead.
                var exitCode = Caller.HasExited ? Caller.ExitCode.ToString() : "unavailable (caller still running)";
                var stdout = CaptureCallerOutput(callerOutput);
                var stderr = CaptureCallerOutput(callerError);
                var receiptPath = Path.Combine(Root, "launch-receipt.json");
                string receipt;
                try { receipt = File.ReadAllText(receiptPath); }
                catch (Exception receiptFailure) when (receiptFailure is IOException or UnauthorizedAccessException)
                {
                    receipt = "unavailable: " + receiptFailure.Message;
                }
                var diagnosticPath = Path.Combine(Root, "launch-identity-error.json");
                string diagnostic;
                try { diagnostic = File.ReadAllText(diagnosticPath); }
                catch (Exception diagnosticFailure) when (diagnosticFailure is IOException or UnauthorizedAccessException)
                {
                    diagnostic = "unavailable: " + diagnosticFailure.Message;
                }
                throw new InvalidOperationException(
                    $"Caller did not publish launch-identity.json; expected path: {identityPath}." + Environment.NewLine +
                    $"Caller exit code: {exitCode}" + Environment.NewLine +
                    $"Caller stdout: {stdout}" + Environment.NewLine +
                    $"Caller stderr: {stderr}" + Environment.NewLine +
                    $"Launch receipt ({receiptPath}): {receipt}" + Environment.NewLine +
                    $"Identity diagnostic ({diagnosticPath}): {diagnostic}", failure);
            }
            using var identity = JsonDocument.Parse(identityText);
            var process = Process.GetProcessById(identity.RootElement.GetProperty("pid").GetInt32());
            try
            {
                // Retain the handle and validate start time/image before using the pid.
                _ = process.SafeHandle;
                Assert.Equal(identity.RootElement.GetProperty("startedAt").GetDateTime(), process.StartTime.ToUniversalTime());
                var image = ProcessImagePathResolver.Resolve(process.SafeHandle);
                Assert.Equal(ProcessImagePathResolver.Status.Resolved, image.Status);
                Assert.Equal(identity.RootElement.GetProperty("imagePath").GetString(), image.Path, ignoreCase: true);
                return process;
            }
            catch { process.Dispose(); throw; }
        }

        private string CaptureCallerOutput(Task<string>? capture)
        {
            if (capture is null) return "unavailable (capture not started)";
            try { return capture.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult(); }
            catch (Exception failure) { return "unavailable: " + failure.Message; }
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            var failures = new List<Exception>();
            Attempt(() =>
            {
                // Let the caller finish publishing child ownership before reading
                // its record. Both output pipes drain from the moment of acquisition.
                if (Caller is not null)
                    Assert.True(Caller.WaitForExit(60000), "CLI caller did not exit during cleanup.");
            });
            Attempt(() =>
            {
                // Once a caller started, absence of its ownership record cannot
                // establish that no child was launched. Fail closed in that case.
                if (Caller is not null)
                {
                    using var conductor = OpenConductor();
                    // Validate and retain the owned identity before releasing the
                    // probe's lifecycle gate; Ready alone does not mean completion.
                    beforeConductorRelease?.Invoke(this, conductor);
                    Release.Set();
                    Assert.True(conductor.WaitForExit(60000), "Test-owned fake conductor did not exit during cleanup.");
                    if (conductor.ExitCode != 0)
                        throw new InvalidOperationException(
                            $"Test-owned fake conductor did not complete cooperatively; exit code: {conductor.ExitCode}.");
                }
            });
            Attempt(() =>
            {
                var drains = new[] { callerOutput, callerError }.OfType<Task<string>>();
                Task.WhenAll(drains).WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
            });
            Attempt(() => Caller?.Dispose());
            Attempt(() => Ready?.Dispose());
            // Failed completion keeps Root and its identity records available;
            // deletion must never replace the failure that prevented safe cleanup.
            if (failures.Count == 0)
                Attempt(() => beforeRootRemoval?.Invoke(this));
            Attempt(() => Release?.Dispose());
            Attempt(() => ReleaseWatchdogExpired?.Dispose());
            if (failures.Count == 0 && Directory.Exists(Root))
                Attempt(() => Directory.Delete(Root, recursive: true));
            if (failures.Count != 0)
                throw new AggregateException("Fixture cleanup failed; root: " + Root, failures);

            void Attempt(Action action)
            {
                try { action(); }
                catch (Exception failure) { failures.Add(failure); }
            }
        }
    }
}
