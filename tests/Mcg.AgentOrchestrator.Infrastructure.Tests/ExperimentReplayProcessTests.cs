using System.Diagnostics;
using System.Text;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Win32.SafeHandles;
using Xunit;

// Parallel-safe Windows contract tests: private job, async-local observer, unique event and temp root.
public sealed class ExperimentReplayProcessTests
{
    public static bool IsWindows => OperatingSystem.IsWindows();

    [Theory(Timeout = TestHangGuard.WholeTestMilliseconds,
        Skip = "Requires Windows job accounting.", SkipUnless = nameof(IsWindows))]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunAsync_ChildBurst_ReconcilesObservedCountsAndResidual(bool loseChildren)
    {
        using var fixture = new ExperimentReplayTestData();
        SafeFileHandle? independent = null;
        try
        {
            using var observation = ExperimentReplayProcess.ObserveForTests((group, _, _) =>
            {
                if (independent is null) Assert.True(group.TryDuplicateAccountingHandle(out independent));
            }, image => !loseChildren || image.Equals(PowerShell, StringComparison.OrdinalIgnoreCase));
            // Known, bounded native child workload. No wait or assertion depends on catching a polling window.
            var script = "for ($j = 0; $j -lt 8; $j++) { " + StartChildScript("exit 0", shortLived: true) +
                "$p.WaitForExit(); [void]$out.GetAwaiter().GetResult(); [void]$err.GetAwaiter().GetResult(); " +
                "if ($p.ExitCode -ne 0) { throw 'child failed' }; $p.Dispose(); } exit 0";
            var result = await ExperimentReplayProcess.RunAsync(PowerShell,
                ["-NoProfile", "-NonInteractive", "-EncodedCommand", Encode(script)], fixture.Root,
                Path.Combine(fixture.Root, "burst"), TestHangGuard.HangSafetyBound, default);

            Assert.Equal(0, result.ExitCode);
            Assert.Equal("exited", result.State);
            var resources = Assert.IsType<ExperimentReplayResources>(result.Resources);
            Assert.True(resources.AggregateAccountingValid, LaunchDiagnostics(result));
            Assert.True(resources.AccountingEvidence!.Complete);
            Assert.Equal(9u, resources.TotalProcesses); // Root plus exactly eight children, including already-exited children.
            Assert.Equal(9L, resources.ObservedProcesses + resources.UnattributedProcesses!.Value);
            Assert.NotNull(independent);
            Assert.True(OwnedProcessGroup.WaitForJobExit(independent, TestHangGuard.HangSafetyBound));
            Assert.True(OwnedProcessGroup.TryGetTotalProcesses(independent, out var total));
            Assert.Equal(total, resources.TotalProcesses);
            Assert.True(OwnedProcessGroup.TryReadAccounting(independent, out var ledger));
            Assert.Equal(ledger.CpuMilliseconds, resources.CpuMilliseconds);
            Assert.Equal(ledger.PeakMemoryBytes, resources.PeakMemoryBytes);
            Assert.Equal(10, resources.ObservationEvidence!.IntervalMilliseconds);
            Assert.Equal("pid+creation-time", resources.ObservationEvidence.IdentityKey);
            Assert.Equal("held-handle-job-membership", resources.ObservationEvidence.MembershipCheck);
            if (loseChildren)
            {
                Assert.Equal(1, resources.ObservedProcesses);
                Assert.Equal(8, resources.UnattributedProcesses);
                Assert.False(resources.LaunchAttributionComplete);
                Assert.False(resources.ExecutableComparisonsValid);
                Assert.False(resources.AllChildPrioritiesVerified);
                Assert.Contains("All-child below-normal priority unverified", result.Reason);
                Assert.Single(resources.LaunchesByExecutable);
            }
        }
        finally { independent?.Dispose(); }
    }

    [Theory]
    [InlineData("assignment")]
    [InlineData("breakaway")]
    [InlineData("handle")]
    [InlineData("cleanup")]
    [InlineData("accounting")]
    [InlineData("total")]
    [InlineData("negative-residual")]
    [InlineData("negative-count")]
    [InlineData("missing-ledger")]
    public void AccountingFailure_BrokenEvidence_FailsClosed(string defect)
    {
        var resources = ExperimentReplayTestData.Execution(new(0, "case", "AA", 0, "A", false)).Resources!;
        Assert.Null(ExperimentReplayProcess.AccountingFailure(resources));
        var evidence = resources.AccountingEvidence!;
        resources = defect switch
        {
            "assignment" => resources with { AccountingEvidence = evidence with { SuspendedRootAssignedBeforeResume = false } },
            "breakaway" => resources with { AccountingEvidence = evidence with { BreakawayDisallowed = false } },
            "handle" => resources with { AccountingEvidence = evidence with { RetainedHandle = false } },
            "cleanup" => resources with { AccountingEvidence = evidence with { EmptyJobConfirmed = false } },
            "accounting" => resources with { AccountingEvidence = evidence with { FinalAccountingRead = false } },
            "total" => resources with { AccountingEvidence = evidence with { TotalProcessesRead = false } },
            "negative-residual" => resources with { LaunchesByExecutable = new Dictionary<string, int> { ["dotnet"] = 2 } },
            "negative-count" => resources with { LaunchesByExecutable = new Dictionary<string, int> { ["dotnet"] = -1 } },
            "missing-ledger" => null,
            _ => throw new InvalidOperationException(defect)
        };
        Assert.NotNull(ExperimentReplayProcess.AccountingFailure(resources));
        Assert.False(resources?.AggregateAccountingValid ?? false);
        Assert.False(resources?.ExecutableComparisonsValid ?? false);
    }

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
            StartChildScript(childScript) +
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
            : StartChildScript(childScript) +
                "$p.WaitForExit(); [void]$out.GetAwaiter().GetResult(); [void]$err.GetAwaiter().GetResult(); exit $p.ExitCode";
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

            Assert.True(count == maximumObserved,
                $"Expected {count} observed identities, got {maximumObserved}. {LaunchDiagnostics(result)}");
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
            release.Set();
            ownedGroup?.TerminateAndConfirmEmpty(TestHangGuard.HangSafetyBound);
            independentObserver?.Dispose();
        }
    }

    private static void AssertResources(ExperimentReplayProcess.Result result, int count)
    {
        var resources = Assert.IsType<ExperimentReplayResources>(result.Resources);
        Assert.True(resources.LaunchAttributionComplete);
        Assert.True(resources.AggregateAccountingValid);
        Assert.True(resources.AllChildPrioritiesVerified);
        Assert.True(resources.ExecutableComparisonsValid);
        Assert.Equal(0, resources.UnattributedProcesses);
        Assert.True((uint)count == resources.TotalProcesses, LaunchDiagnostics(result));
        Assert.Equal((uint)count, resources.TotalProcesses);
        Assert.Equal(count, Assert.Single(resources.LaunchesByExecutable).Value);
        Assert.True(resources.CpuMilliseconds >= 0);
        Assert.True(resources.PeakMemoryBytes > 0);
        Assert.StartsWith("job-object/", resources.AccountingSource);
        Assert.Equal("BelowNormal", result.Priority);
    }

    private static string PowerShell => Path.Combine(Environment.SystemDirectory,
        "WindowsPowerShell", "v1.0", "powershell.exe");

    // Match the runner root's pipe contract for the nested launch as well. No stream
    // depends on an ambient console, and output drains start before the child waits.
    private static string StartChildScript(string command, bool shortLived = false) =>
        $"$i = [Diagnostics.ProcessStartInfo]::new('{(shortLived ? Path.Combine(Environment.SystemDirectory, "cmd.exe") : PowerShell)}'); $i.UseShellExecute = $false; $i.CreateNoWindow = $true; " +
        "$i.RedirectStandardInput = $true; $i.RedirectStandardOutput = $true; $i.RedirectStandardError = $true; " +
        $"$i.Arguments = '{(shortLived ? "/d /c exit 0" : "-NoProfile -NonInteractive -EncodedCommand " + Encode(command))}'; $p = [Diagnostics.Process]::Start($i); " +
        "$p.StandardInput.Close(); $out = $p.StandardOutput.ReadToEndAsync(); $err = $p.StandardError.ReadToEndAsync(); ";

    private static string LaunchDiagnostics(ExperimentReplayProcess.Result result) =>
        $"State={result.State}; reason={result.Reason}; total={result.Resources?.TotalProcesses}; launches=" +
        string.Join(", ", result.Resources?.LaunchesByExecutable.Select(pair => $"{pair.Key}={pair.Value}") ?? []);

    private static string Encode(string command) => Convert.ToBase64String(Encoding.Unicode.GetBytes(command));
}
