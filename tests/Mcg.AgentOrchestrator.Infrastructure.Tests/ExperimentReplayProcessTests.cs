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
