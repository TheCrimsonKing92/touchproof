using System.Diagnostics;
using System.Text;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Win32.SafeHandles;
using Xunit;

// Parallel-safe Windows contract tests: private job, async-local observer, unique event and temp root.
public sealed class ExperimentReplayProcessTests
{
    public static bool IsWindows => OperatingSystem.IsWindows();

    [Fact(Timeout = TestHangGuard.WholeTestMilliseconds,
        Skip = "Requires Windows job accounting.", SkipUnless = nameof(IsWindows))]
    public async Task RunAsync_ExitZero_RetainsFinalAccounting()
    {
        using var fixture = new ExperimentReplayTestData();
        var result = await ExperimentReplayProcess.RunAsync(PowerShell,
            ["-NoProfile", "-NonInteractive", "-Command", "exit 0"], fixture.Root,
            Path.Combine(fixture.Root, "measurement"), TestHangGuard.HangSafetyBound, default);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("exited", result.State);
        Assert.Null(result.Reason);
        AssertResources(result, 1);
        var inspection = Assert.IsType<ExperimentReplayProcess.ExitMembership>(result.ExitInspection);
        Assert.True(inspection.RootProcessId > 0);
        Assert.All(inspection.ReportedProcessIds, id => Assert.Equal(inspection.RootProcessId, id));
        Assert.Empty(inspection.LiveDescendants);
        Assert.Empty(inspection.UnverifiedProcessIds);
    }

    [Fact(Timeout = TestHangGuard.WholeTestMilliseconds,
        Skip = "Requires Windows job accounting.", SkipUnless = nameof(IsWindows))]
    public async Task InspectExitedJob_RetainedRootMembership_DoesNotReportLiveDescendants()
    {
        using var fixture = new ExperimentReplayTestData();
        var info = new ProcessStartInfo(PowerShell) { WorkingDirectory = fixture.Root, UseShellExecute = false,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-Command", "exit 0" }) info.ArgumentList.Add(argument);
        AcceptanceHermeticEnvironment.ConfigureHermeticVerificationEnvironment(info.Environment, fixture.Root);
        using var pending = OwnedProcessGroup.StartSuspendedContainedRedirected(info);
        try
        {
            pending.StandardInput.Close();
            var output = pending.StandardOutput.ReadToEndAsync();
            var error = pending.StandardError.ReadToEndAsync();
            // Capture a REAL nonempty job snapshot while suspended, then retain it across
            // exit. This deterministically supplies the stale-root condition, without sleeps.
            Assert.True(pending.Group.TryGetActiveProcessIds(out var reported));
            Assert.Equal(pending.Process.Id, Assert.Single(reported));
            Assert.True(pending.Group.Contains(pending.Process.SafeHandle));
            var start = pending.Process.StartTime.ToUniversalTime();
            pending.Resume();
            using var deadline = new CancellationTokenSource(TestHangGuard.HangSafetyBound);
            await pending.Process.WaitForExitAsync(deadline.Token);
            await Task.WhenAll(output, error).WaitAsync(deadline.Token);
            Assert.Equal(0, pending.Process.ExitCode);

            var inspection = ExperimentReplayProcess.InspectExitedJob(pending.Group, pending.Process, reported);
            // Negative control: the previous active.Count > 0 rule labels this exact
            // root-only snapshot as live descendants despite the signaled exit handle.
            Assert.NotEmpty(inspection.ReportedProcessIds);
            Assert.Equal(pending.Process.Id, inspection.RootProcessId);
            Assert.Equal(start, inspection.RootStartedAtUtc);
            Assert.Empty(inspection.LiveDescendants);
            Assert.Empty(inspection.UnverifiedProcessIds);
        }
        finally
        {
            Assert.True(pending.Group.TerminateAndConfirmEmpty(TestHangGuard.HangSafetyBound));
        }
    }

    [Fact(Timeout = TestHangGuard.WholeTestMilliseconds,
        Skip = "Requires Windows job accounting.", SkipUnless = nameof(IsWindows))]
    public async Task RunAsync_SurvivingDescendant_FailsAndConfirmsCleanup()
    {
        using var fixture = new ExperimentReplayTestData();
        var rootIdPath = Path.Combine(fixture.Root, "root.pid");
        var eventPrefix = "replay-descendant-" + Guid.NewGuid().ToString("N");
        using var releaseRoot = new EventWaitHandle(false, EventResetMode.ManualReset, eventPrefix + "-root");
        using var releaseChild = new EventWaitHandle(false, EventResetMode.ManualReset, eventPrefix + "-child");
        var childScript = $"$e = [Threading.EventWaitHandle]::OpenExisting('{eventPrefix}-child'); [void]$e.WaitOne(); exit 0";
        var rootScript = $"[IO.File]::WriteAllText('{rootIdPath.Replace("'", "''")}', [string]$PID); " +
            $"$i = [Diagnostics.ProcessStartInfo]::new('{PowerShell}'); $i.UseShellExecute = $false; $i.CreateNoWindow = $true; " +
            $"$i.Arguments = '-NoProfile -NonInteractive -EncodedCommand {Encode(childScript)}'; $p = [Diagnostics.Process]::Start($i); " +
            $"$e = [Threading.EventWaitHandle]::OpenExisting('{eventPrefix}-root'); [void]$e.WaitOne(); exit 0";
        OwnedProcessGroup? ownedGroup = null;
        Process? root = null;
        Process? child = null;
        try
        {
            using var observation = ExperimentReplayProcess.ObserveForTests((group, _, observed) =>
            {
                ownedGroup = group;
                if (observed != 2 || root is not null) return;
                Assert.True(group.TryGetActiveProcessIds(out var ids));
                var rootId = int.Parse(File.ReadAllText(rootIdPath));
                Assert.Contains(rootId, ids);
                root = Process.GetProcessById(rootId);
                child = Process.GetProcessById(Assert.Single(ids.Where(id => id != rootId)));
                Assert.True(group.Contains(root.SafeHandle));
                Assert.True(group.Contains(child.SafeHandle));
                Assert.False(root.HasExited);
                Assert.False(child.HasExited);
                // Both identities have been sampled. Only the root may exit; the child
                // remains blocked until production job cleanup terminates it.
                releaseRoot.Set();
            });
            var result = await ExperimentReplayProcess.RunAsync(PowerShell,
                ["-NoProfile", "-NonInteractive", "-EncodedCommand", Encode(rootScript)], fixture.Root,
                Path.Combine(fixture.Root, "measurement"), TestHangGuard.HangSafetyBound, default);

            Assert.NotNull(root);
            Assert.NotNull(child);
            Assert.Equal(0, result.ExitCode);
            Assert.Equal("apparatus-failed", result.State);
            Assert.Equal("Procedure exited with live descendants.", result.Reason);
            AssertResources(result, 2);
            var inspection = Assert.IsType<ExperimentReplayProcess.ExitMembership>(result.ExitInspection);
            Assert.Equal(root.Id, inspection.RootProcessId);
            Assert.Equal(root.StartTime.ToUniversalTime(), inspection.RootStartedAtUtc);
            Assert.Contains(child.Id, inspection.ReportedProcessIds);
            var survivor = Assert.Single(inspection.LiveDescendants);
            Assert.Equal(child.Id, survivor.ProcessId);
            Assert.Equal(child.StartTime.ToUniversalTime(), survivor.StartedAt.UtcDateTime);
            Assert.Equal(PowerShell, survivor.ExecutablePath, ignoreCase: true);
            Assert.Empty(inspection.UnverifiedProcessIds);
            Assert.True(root.HasExited);
            Assert.True(child.HasExited); // Cleanup killed the verified surviving identity.
            Assert.NotNull(ownedGroup);
            Assert.False(ownedGroup.TryReadAccounting(out _));
        }
        finally
        {
            releaseRoot.Set();
            releaseChild.Set();
            ownedGroup?.TerminateAndConfirmEmpty(TestHangGuard.HangSafetyBound);
            root?.Dispose();
            child?.Dispose();
        }
    }

    [Theory(Timeout = TestHangGuard.WholeTestMilliseconds,
        Skip = "Requires Windows job accounting.", SkipUnless = nameof(IsWindows))]
    [InlineData("exited", 2)]
    [InlineData("cancelled", 2)]
    [InlineData("timed-out", 1)]
    public async Task RunAsync_JobCleanup_ReadsFinalTotalsAndReleasesObserver(string state, int count)
    {
        using var fixture = new ExperimentReplayTestData();
        using var cancellation = new CancellationTokenSource();
        var eventName = "replay-accounting-" + Guid.NewGuid().ToString("N");
        using var release = new EventWaitHandle(false, EventResetMode.ManualReset, eventName);
        var childScript = $"$e = [Threading.EventWaitHandle]::OpenExisting('{eventName}'); [void]$e.WaitOne(); $e.Dispose(); exit 0";
        var rootScript = count == 1 ? "[void]([Threading.ManualResetEvent]::new($false)).WaitOne()"
            : $"$i = [Diagnostics.ProcessStartInfo]::new('{PowerShell}'); $i.UseShellExecute = $false; $i.CreateNoWindow = $true; $i.Arguments = '-NoProfile -NonInteractive -EncodedCommand {Encode(childScript)}'; $p = [Diagnostics.Process]::Start($i); $p.WaitForExit(); exit $p.ExitCode";
        OwnedProcessGroup? ownedGroup = null;
        SafeFileHandle? runnerObserver = null;
        SafeFileHandle? independentObserver = null;
        var maximumObserved = 0;
        try
        {
            using var observation = ExperimentReplayProcess.ObserveForTests((group, observer, observed) =>
            {
                ownedGroup = group;
                runnerObserver = observer;
                if (independentObserver is null)
                    Assert.True(group.TryDuplicateAccountingHandle(out independentObserver));
                Assert.False(observer.IsClosed);
                Assert.False(observer.IsInvalid);
                maximumObserved = Math.Max(maximumObserved, observed);
                // Arrange actual membership BEFORE cancellation; successful children wait
                // until both executable identities have been observed by the real sampler.
                if (observed != count) return;
                if (state == "cancelled") cancellation.Cancel();
                else if (state == "exited") release.Set();
            });
            var result = await ExperimentReplayProcess.RunAsync(PowerShell,
                ["-NoProfile", "-NonInteractive", "-EncodedCommand", Encode(rootScript)], fixture.Root,
                Path.Combine(fixture.Root, "measurement"),
                state == "timed-out" ? TimeSpan.Zero : TestHangGuard.HangSafetyBound, cancellation.Token);

            Assert.Equal(count, maximumObserved);
            Assert.Equal(state, result.State);
            if (state == "exited") { Assert.Equal(0, result.ExitCode); Assert.Null(result.Reason); }
            else { Assert.Null(result.ExitCode); Assert.Equal(state, result.Reason); }
            AssertResources(result, count);
            Assert.NotNull(ownedGroup);
            Assert.False(ownedGroup.TryReadAccounting(out _)); // The owning group WAS disposed.
            Assert.NotNull(runnerObserver);
            Assert.True(runnerObserver.IsClosed); // RunAsync released its retained handle.
            Assert.NotNull(independentObserver);
            Assert.False(independentObserver.IsClosed); // Cleanup cannot rely on last-handle close.
            Assert.True(OwnedProcessGroup.WaitForJobExit(independentObserver, TestHangGuard.HangSafetyBound));
            Assert.True(OwnedProcessGroup.TryGetTotalProcesses(independentObserver, out var total));
            Assert.Equal((uint)count, total);
            Assert.True(OwnedProcessGroup.TryReadAccounting(independentObserver, out var final));
            Assert.Equal(final.CpuMilliseconds, result.Resources!.CpuMilliseconds);
            Assert.Equal(final.PeakMemoryBytes, result.Resources.PeakMemoryBytes);
        }
        finally
        {
            // Also clean up if a test assertion throws while observation retains the job.
            ownedGroup?.TerminateAndConfirmEmpty(TestHangGuard.HangSafetyBound);
            independentObserver?.Dispose();
        }
    }

    private static void AssertResources(ExperimentReplayProcess.Result result, int count)
    {
        var resources = Assert.IsType<ExperimentReplayResources>(result.Resources);
        Assert.True(resources.LaunchAttributionComplete);
        Assert.Equal((uint)count, resources.TotalProcesses);
        Assert.Equal(count, Assert.Single(resources.LaunchesByExecutable).Value);
        Assert.True(resources.CpuMilliseconds >= 0);
        Assert.True(resources.PeakMemoryBytes > 0);
        Assert.StartsWith("job-object/", resources.AccountingSource);
        Assert.Equal("BelowNormal", result.Priority);
    }

    private static string PowerShell => Path.Combine(Environment.SystemDirectory,
        "WindowsPowerShell", "v1.0", "powershell.exe");

    private static string Encode(string command) => Convert.ToBase64String(Encoding.Unicode.GetBytes(command));
}
