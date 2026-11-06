using Mcg.AgentOrchestrator.Infrastructure;
using Mcg.AgentOrchestrator.App.Orchestration;

[Xunit.Collection(TestCollections.ProcessSpawning)]
public sealed class ProcessTreeGuiSuppressionTestsLaunchTelemetryCoverage
{
    public static bool IsWindows => OperatingSystem.IsWindows();
    [Xunit.Fact(Skip = "Requires Windows launch telemetry.", SkipUnless = nameof(IsWindows))]
    public void ConvertedEntryPoints_RecordOneSamplePerLaunch()
    {
        var root = ProcessTreeGuiSuppressionTestsProcessErrorMode.CreateRoot();
        var prior = Volatile.Read(ref GitCli.LaunchObserver);
        var notifications = new List<string>();
        Action<string> observer = subcommand => { prior?.Invoke(subcommand); notifications.Add(subcommand); };
        Assert.Same(prior, Interlocked.CompareExchange(ref GitCli.LaunchObserver, observer, prior));
        try
        {
            Assert.True(InfrastructureTestSupport.RunGitProbe(root, ["init", "-q"]).Succeeded);
            notifications.Clear();
            using var capture = LaunchLockTelemetry.BeginCapture();
            Assert.True(GitCli.Run(root, "--version").Succeeded);
            var reader = GitRevisionReader.ReaderProcess.Start(Path.Combine(root, ".git"));
            reader.Terminate();
            foreach (var entry in new[] { LaunchLockEntryPoint.Start, LaunchLockEntryPoint.AcquireErrorModeForChildSpawn })
            {
                using var child = HandleListProcessLauncher.Start(ProcessTreeGuiSuppressionTestsProcessErrorMode.ExitCommand(), entry);
                Assert.True(child.WaitForExit(120_000), "Telemetry child did not exit.");
            }
            using (var child = OwnedProcessGroup.StartSuspended(ProcessTreeGuiSuppressionTestsProcessErrorMode.ExitCommand()))
            { child.Resume(); Assert.True(child.WaitForOwnedExit(TimeSpan.FromSeconds(120)), "Owned telemetry child did not exit."); }
            using (var child = OwnedProcessGroup.StartSuspendedWithFileCapture(ProcessTreeGuiSuppressionTestsProcessErrorMode.ExitCommand(),
                Path.Combine(root, "capture.out"), Path.Combine(root, "capture.err")))
            { child.Resume(); Assert.True(child.WaitForOwnedExit(TimeSpan.FromSeconds(120)), "Capture telemetry child did not exit."); }
            var info = ProcessTreeGuiSuppressionTestsProcessErrorMode.ExitCommand();
            info.RedirectStandardInput = info.RedirectStandardOutput = info.RedirectStandardError = true;
            using (var child = OwnedProcessGroup.StartSuspendedContainedRedirected(info))
            { child.Resume(); Assert.True(child.Process.WaitForExit(120_000), "Redirected telemetry child did not exit."); }
            var stdout = WindowsHandleListProcessLauncher.CreateInheritedOutputFile(Path.Combine(root, "app.out"));
            var stderr = WindowsHandleListProcessLauncher.CreateInheritedOutputFile(Path.Combine(root, "app.err"));
            try
            {
                var outcome = WindowsHandleListProcessLauncher.Launch(HandleListProcessLauncher.ResolveApplication(info.FileName),
                    HandleListProcessLauncher.CommandLine(info), HandleListProcessLauncher.EnvironmentBlock(info.Environment), root,
                    HandleListProcessLauncher.UnicodeEnvironment | ChildConsoleLaunchPolicy.CreateNoWindow, stdout, stderr);
                Assert.True(outcome.Created, $"App telemetry launch failed: {outcome.NativeError}");
                try
                {
                    // Keep the native handle held while opening and waiting on the child;
                    // closing it alone neither waits for exit nor releases its working directory.
                    using var child = System.Diagnostics.Process.GetProcessById(outcome.ProcessId);
                    var identity = TestOwnedProcessStop.Identify(child);
                    try { Assert.True(child.WaitForExit(120_000), "Detached telemetry child did not exit."); }
                    finally { TestOwnedProcessStop.StopTreeIfSame(identity); }
                }
                finally
                {
                    WindowsHandleListProcessLauncher.CloseHandle(outcome.ThreadHandle);
                    WindowsHandleListProcessLauncher.CloseHandle(outcome.ProcessHandle);
                }
            }
            finally { WindowsHandleListProcessLauncher.CloseHandle(stdout); WindowsHandleListProcessLauncher.CloseHandle(stderr); }
            var snapshot = capture.Snapshot();
            Assert.Equal(8, snapshot.Launches);
            Assert.Equal([1L, 3L, 1L, 2L, 1L], snapshot.ByEntry);
            Assert.Equal(["--version", "cat-file"], notifications);
            Assert.Equal(8, capture.Samples.Count);
            Assert.All(capture.Samples, sample => { Assert.Equal(0, sample.WaitTicks); Assert.True(sample.HoldTicks >= sample.CreateTicks); });
            var summary = LaunchLockTelemetry.FormatSummary(snapshot, Environment.ProcessId);
            foreach (var field in new[] { "launches=", "wait_", "hold_", "create_", "by_entry=" }) Assert.Contains(field, summary);
        }
        finally
        {
            Assert.Same(observer, Interlocked.CompareExchange(ref GitCli.LaunchObserver, prior, observer));
            Directory.Delete(root, true);
        }
    }
}
