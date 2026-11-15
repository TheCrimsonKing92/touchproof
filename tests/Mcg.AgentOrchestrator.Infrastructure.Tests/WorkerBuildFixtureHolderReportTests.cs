using System.Diagnostics;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each test owns its repository, native jobs, capture files and named events.
public sealed class WorkerBuildFixtureHolderReportTests
{
    [Fact(Timeout = TestHangGuard.WholeTestMilliseconds)]
    public async Task CleanupFailure_ControlledReader_NamesLiveChildPid()
    {
        var root = Path.Combine(Path.GetTempPath(), "mcg-holder-child-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var suffix = Guid.NewGuid().ToString("N");
        using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, "Local\\mcg-holder-ready-" + suffix);
        using var release = new EventWaitHandle(false, EventResetMode.ManualReset, "Local\\mcg-holder-release-" + suffix);
        OwnedProcessGroup.SuspendedProcessStart? child = null;
        try
        {
            child = StartSuspended(root, "child",
                "$r=[Threading.EventWaitHandle]::OpenExisting('Local\\mcg-holder-ready-" + suffix + "');$null=$r.Set();" +
                "$g=[Threading.EventWaitHandle]::OpenExisting('Local\\mcg-holder-release-" + suffix + "');" +
                "if(-not $g.WaitOne(240000)){throw 'holder child release event did not arrive'}");
            child.Resume();
            Assert.True(await Task.Run(() => ready.WaitOne(TestHangGuard.HangSafetyBound)),
                "Holder child did not signal its named readiness event.");
            var childId = child.Process.Id;
            var childName = child.Process.ProcessName;
            int? observedAncestor = null;
            var result = await Task.Run(() => CaptureCleanupFailure(ancestor =>
            {
                observedAncestor = ancestor;
                return [childId];
            }));

            Assert.Equal(result.WorkerId, observedAncestor);
            Assert.StartsWith($"Residual root '{result.Root}' attributes=", result.Residual);
            Assert.StartsWith($"Worker build fixture cleanup failed. {result.Residual}{Environment.NewLine}Holder report for root", result.Error.Message);
            Assert.Contains($"pid={childId} name={childName}", result.Error.Message);
            Assert.False(child.Process.HasExited);
        }
        finally
        {
            try
            {
                if (child is not null)
                {
                    if (!child.Process.HasExited) child.Process.Kill();
                    await TestHangGuard.WaitAsync(child.Process.WaitForExitAsync(), "holder child exit after exact-pid termination");
                }
            }
            finally
            {
                child?.Dispose();
                SharedTestSupport.RemoveTempDirectory(root);
            }
        }
    }

    [Fact(Timeout = TestHangGuard.WholeTestMilliseconds)]
    public async Task CleanupFailure_EmptyReader_StatesNoLiveDescendant()
    {
        var result = await Task.Run(() => CaptureCleanupFailure(_ => []));
        Assert.StartsWith($"Residual root '{result.Root}' attributes=", result.Residual);
        Assert.StartsWith($"Worker build fixture cleanup failed. {result.Residual}{Environment.NewLine}Holder report for root", result.Error.Message);
        Assert.Contains("No live descendant was found.", result.Error.Message);
    }

    [Fact(Timeout = TestHangGuard.WholeTestMilliseconds)]
    public async Task CleanupFailure_ReaderThrows_ReportsEnumerationFailure()
    {
        var result = await Task.Run(() => CaptureCleanupFailure(_ => throw new IOException("reader unavailable")));
        Assert.StartsWith($"Worker build fixture cleanup failed. Residual root '{result.Root}' attributes=", result.Error.Message);
        Assert.Contains("live-descendant enumeration failed: IOException: reader unavailable", result.Error.Message);
        Assert.DoesNotContain("No live descendant was found.", result.Error.Message);
        Assert.Contains(result.Error.Flatten().InnerExceptions, error => error.Message == "controlled cleanup failure");
    }

    [Fact]
    public void Describe_NameLookupFails_KeepsPidWithUnavailableName()
    {
        var report = WorkerBuildFixtureHolderReport.Describe(10, "fixture-root", _ => [20],
            _ => throw new InvalidOperationException("process exited"));
        Assert.Contains("pid=20 name=<unavailable>", report);
        Assert.DoesNotContain("No live descendant was found.", report);
    }

    [Fact]
    public void Describe_ListsEveryDistinctPidInAscendingOrder()
    {
        var report = WorkerBuildFixtureHolderReport.Describe(10, "fixture-root",
            _ => [20, 12, 19, 11, 18, 13, 17, 14, 16, 15, 20], id => "process-" + id);
        var expected = string.Join("; ", Enumerable.Range(11, 10).Select(id => $"pid={id} name=process-{id}"));
        Assert.EndsWith(expected, report);
        Assert.Contains("filesystem-handle ownership is unverified", report);
    }

    [Fact]
    public void Describe_NoWorker_DoesNotEnumerate()
    {
        var called = false;
        var report = WorkerBuildFixtureHolderReport.Describe(null, "fixture-root", _ =>
        {
            called = true;
            return [];
        });
        Assert.False(called);
        Assert.Contains("No worker process was started; descendants were not enumerated.", report);
        Assert.DoesNotContain("No live descendant was found.", report);
    }

    private static (AggregateException Error, string Root, int WorkerId, string Residual) CaptureCleanupFailure(
        Func<int, IReadOnlyList<int>> reader)
    {
        var fixture = new WorkerBuildRequestFixture(compilerMode: WorkerBuildRequestFixture.CompilerMode.Fake);
        var disposeAttempted = false;
        try
        {
            // Never resume this worker: fixture teardown still has a genuine pid to capture before killing its job.
            fixture.Worker = StartSuspended(fixture.Root, "worker", "exit 0");
            var workerId = fixture.Worker.Process.Id;
            fixture.LiveDescendantReader = reader;
            using var failure = fixture.Lifetime.Token.Register(() => throw new IOException("controlled cleanup failure"));
            disposeAttempted = true;
            var error = Assert.Throws<AggregateException>(fixture.Dispose);
            return (error, fixture.Root, workerId, SharedTestSupport.DescribeResidual(fixture.Root));
        }
        finally
        {
            try { if (!disposeAttempted) fixture.Dispose(); }
            finally { SharedTestSupport.RemoveTempDirectory(fixture.Root); }
        }
    }

    private static OwnedProcessGroup.SuspendedProcessStart StartSuspended(string root, string label, string script)
    {
        var start = new ProcessStartInfo(WorkerShell.Executable)
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = root
        };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-Command", script }) start.ArgumentList.Add(argument);
        return OwnedProcessGroup.StartSuspendedContainedWithFileCapture(start,
            Path.Combine(root, label + ".out.log"), Path.Combine(root, label + ".err.log"));
    }
}
