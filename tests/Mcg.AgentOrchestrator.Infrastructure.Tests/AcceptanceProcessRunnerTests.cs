using System.Diagnostics;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each case owns its working directory, child and uniquely named capture files.
// No process-wide environment, hooks or collection state are changed.
public sealed class AcceptanceProcessRunnerTests
{
    // Pre-extraction baseline: d2ecd0a2e GoalAcceptanceVerifier.ProcessRunner.cs delegates
    // to AcceptanceCheckProcessInvoker. Its unchanged result construction preserves stdout,
    // the command exit code, and -1/TimedOut for the injected timeout signal.
    // AcceptanceCaptureDrain.cs keeps default captures iff timeout or exit != 0.
    private const string Stdout = "acceptance-runner-out";
    private const string Stderr = "acceptance-runner-err";
    private static readonly TimeSpan CommandHangFailsafe = TimeSpan.FromMinutes(5);

    [Theory(Timeout = TestHangGuard.WholeTestMilliseconds)]
    [InlineData(0, false)]
    [InlineData(7, true)]
    public async Task Command_exit_preserves_output_code_and_default_capture_lifecycle(
        int exitCode, bool capturesKept)
    {
        var root = Directory.CreateTempSubdirectory("mcg-acceptance-runner-").FullName;
        using var cancellation = new CancellationTokenSource(TestHangGuard.HangSafetyBound);
        GoalAcceptanceVerifier.CommandResult? result = null;
        try
        {
            result = await AcceptanceProcessRunner.RunProcessForTestsAsync(
                ["powershell", "-NoProfile", "-NonInteractive", "-Command",
                    $"[Console]::Out.Write('{Stdout}'); [Console]::Error.Write('{Stderr}'); exit {exitCode}"],
                root, CommandHangFailsafe, cancellationToken: cancellation.Token,
                keepCaptureFiles: null);

            Assert.Equal(exitCode, result.ExitCode);
            Assert.False(result.TimedOut);
            Assert.False(result.CaptureLimited);
            if (OperatingSystem.IsWindows()) Assert.NotNull(result.ResourceAccounting);
            Assert.Equal(Stdout, result.StandardOutput);
            Assert.Equal(Stderr, result.Stderr);
            Assert.Equal(Stdout + Stderr, result.Output);
            Assert.False(string.IsNullOrWhiteSpace(result.StdoutPath));
            Assert.False(string.IsNullOrWhiteSpace(result.StderrPath));
            Assert.NotEqual(result.StdoutPath, result.StderrPath);
            Assert.Equal(capturesKept, File.Exists(result.StdoutPath));
            Assert.Equal(capturesKept, File.Exists(result.StderrPath));
            if (capturesKept)
            {
                Assert.Equal(Stdout, File.ReadAllText(result.StdoutPath!));
                Assert.Equal(Stderr, File.ReadAllText(result.StderrPath!));
            }
        }
        finally
        {
            DeleteCaptureFiles(result);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact(Timeout = TestHangGuard.WholeTestMilliseconds)]
    public async Task Timeout_signal_after_command_start_reports_timeout_and_disposes_child()
    {
        var root = Directory.CreateTempSubdirectory("mcg-acceptance-runner-timeout-").FullName;
        using var cancellation = new CancellationTokenSource(TestHangGuard.HangSafetyBound);
        using var timeoutSignal = new CancellationTokenSource();
        var commandObserved = false;
        var cleanup = new List<AcceptanceProcessCleanupObservation>();
        GoalAcceptanceVerifier.CommandResult? result = null;
        try
        {
            // The child has no natural exit. Trigger timeout only after its identity is
            // observed; the cancellation above bounds a missing start/cleanup event.
            result = await AcceptanceProcessRunner.RunProcessForTestsAsync(
                ["powershell", "-NoProfile", "-NonInteractive", "-Command",
                    "$gate = [System.Threading.ManualResetEvent]::new($false); " +
                    "try { [void]$gate.WaitOne() } finally { $gate.Dispose() }"],
                root, CommandHangFailsafe, cancellationToken: cancellation.Token,
                cleanupObserver: cleanup.Add, timeoutSignal: timeoutSignal.Token,
                commandIdentityObserver: _ =>
                {
                    commandObserved = true;
                    timeoutSignal.Cancel();
                });

            Assert.True(commandObserved, "The command identity was not observed before timeout.");
            Assert.True(result.TimedOut);
            Assert.Equal(-1, result.ExitCode);
            Assert.False(result.CaptureLimited);
            Assert.Equal(CommandHangFailsafe, result.Timeout);
            Assert.Contains(cleanup, observation => observation.Stage == "started");
            var disposed = Assert.Single(cleanup,
                observation => observation.Stage == "owned-child-disposed");
            Assert.True(disposed.ProcessDisposed);
            Assert.False(disposed.RegistryActive);
            Assert.False(disposed.NativeHandleOpen);
        }
        finally
        {
            DeleteCaptureFiles(result);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact(Timeout = TestHangGuard.WholeTestMilliseconds)]
    public async Task Caller_cancellation_before_exit_wait_confirms_owned_tree_exit()
    {
        var root = Directory.CreateTempSubdirectory("mcg-acceptance-runner-cancel-").FullName;
        using var cancellation = new CancellationTokenSource(TestHangGuard.HangSafetyBound);
        Process? launcher = null;
        try
        {
            var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                AcceptanceProcessRunner.RunProcessForTestsAsync(
                    ["powershell", "-NoProfile", "-NonInteractive", "-Command",
                        "$gate = [System.Threading.ManualResetEvent]::new($false); " +
                        "try { [void]$gate.WaitOne() } finally { $gate.Dispose() }"],
                    root, CommandHangFailsafe, cancellationToken: cancellation.Token,
                    cleanupObserver: observation =>
                    {
                        if (observation.Stage != "started") return;
                        // Interrupt the real launch before capture startup/exit wait. Pin the
                        // handle while owned so the assertion cannot observe a recycled PID.
                        launcher = Process.GetProcessById(observation.ProcessId);
                        _ = launcher.SafeHandle;
                        cancellation.Cancel();
                        cancellation.Token.ThrowIfCancellationRequested();
                    }));

            Assert.NotNull(launcher);
            Assert.True(Assert.IsType<bool>(exception.Data["AcceptanceCancellationTreeExitConfirmed"]));
            Assert.Equal(launcher.Id, exception.Data["AcceptanceCancellationProcessId"]);
            Assert.True(launcher.HasExited, "Caller cancellation returned before the owned launcher exited.");
        }
        finally
        {
            launcher?.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact(Timeout = TestHangGuard.WholeTestMilliseconds)]
    public async Task Caller_cancellation_at_final_heartbeat_keeps_custody_until_cleanup()
    {
        var root = Directory.CreateTempSubdirectory("mcg-acceptance-final-cancel-").FullName;
        using var cancellation = new CancellationTokenSource(TestHangGuard.HangSafetyBound);
        var cleanup = new List<AcceptanceProcessCleanupObservation>();
        var finalHeartbeatObserved = false;
        try
        {
            var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                AcceptanceProcessRunner.RunProcessAsync(
                    ["powershell", "-NoProfile", "-NonInteractive", "-Command", "exit 0"],
                    root, CommandHangFailsafe, forceUtf8ConsoleOutput: false,
                    cancellationToken: cancellation.Token,
                    heartbeatContext: new(null, "check", "final-cancellation", null,
                        Path.Combine(root, "heartbeat.json"), null, "exit 0", null),
                    cleanupObserver: observation =>
                    {
                        cleanup.Add(observation);
                        if (observation.Stage != "heartbeat-final") return;
                        Assert.True(observation.RegistryActive);
                        finalHeartbeatObserved = true;
                        cancellation.Cancel();
                        cancellation.Token.ThrowIfCancellationRequested();
                    }));

            Assert.True(finalHeartbeatObserved, "Cancellation must be injected at the final heartbeat.");
            Assert.True(Assert.IsType<bool>(exception.Data["AcceptanceCancellationCleanupRan"]));
            Assert.True(Assert.IsType<bool>(exception.Data["AcceptanceCancellationTreeExitConfirmed"]));
            Assert.False(exception.Data.Contains("AcceptanceCancellationCleanupSkippedReason"));
            Assert.Single(cleanup, observation => observation.Stage == "registration-released");
            var disposed = Assert.Single(cleanup, observation => observation.Stage == "owned-child-disposed");
            Assert.True(disposed.ProcessDisposed);
            Assert.False(disposed.RegistryActive);
            Assert.False(disposed.NativeHandleOpen);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void DeleteCaptureFiles(GoalAcceptanceVerifier.CommandResult? result)
    {
        if (result?.StdoutPath is { } stdout) File.Delete(stdout);
        if (result?.StderrPath is { } stderr) File.Delete(stderr);
    }
}
