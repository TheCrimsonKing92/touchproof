using System.Diagnostics;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: each process is owned by its invocation; no global process observations or env changes.
public sealed class ProviderStatusProcessTests
{
    [Fact]
    public async Task BoundedCapture_DrainsBothStreamsAndClosesInput()
    {
        var result = await WorkerProcessRunner.RunBoundedDirectAsync(Request(
            "if ([Console]::In.ReadToEnd() -ne '') { exit 9 }; [Console]::Out.Write('out'); [Console]::Error.Write('err')"),
            65536, default);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("out", result.StandardOutput);
        Assert.Equal("err", result.StandardError);
        Assert.False(result.CaptureLimited);
        Assert.False(result.TimedOut);
    }

    [Fact]
    public async Task AggregateCeiling_DiscardsPartialOutputAndReapsOwnedRoot()
    {
        bool? reaped = null;
        var result = await WorkerProcessRunner.RunBoundedDirectAsync(Request(
            "[Console]::Out.Write(('x' * 40000)); [Console]::Error.Write(('y' * 40000)); Wait-Event"),
            65536, default, TimeProvider.System, null, exited => reaped = exited);
        Assert.True(result.CaptureLimited);
        Assert.False(result.TimedOut);
        Assert.Empty(result.StandardOutput);
        Assert.Empty(result.StandardError);
        Assert.Equal(true, reaped);
    }

    [Fact]
    public async Task Deadline_DecisionAndCleanupAreObservableWithoutWallClockAssertions()
    {
        var time = new DeadlineTime();
        var started = false;
        bool? reaped = null;
        var result = await WorkerProcessRunner.RunBoundedDirectAsync(Request("Wait-Event"), 65536, default,
            time, _ => { started = true; time.Expire(); return Task.CompletedTask; }, exited => reaped = exited);
        Assert.True(started);
        Assert.Equal(TimeSpan.FromSeconds(10), time.DueTime);
        Assert.True(result.TimedOut);
        Assert.False(result.CaptureLimited);
        Assert.Equal(true, reaped);
    }

    [Fact]
    public async Task CallerCancellation_IsNotAReadinessTimeoutAndStillReapsRoot()
    {
        using var cancellation = new CancellationTokenSource();
        bool? reaped = null;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => WorkerProcessRunner.RunBoundedDirectAsync(
            Request("Wait-Event"), 65536, cancellation.Token, TimeProvider.System,
            _ => { cancellation.Cancel(); return Task.CompletedTask; }, exited => reaped = exited));
        Assert.Equal(true, reaped);
    }

    [Fact]
    public async Task CallerCancellation_ReapsDescendantAfterProbeRootExits()
    {
        if (!OperatingSystem.IsWindows()) return;
        var directory = Directory.CreateTempSubdirectory("mcg-status-probe-");
        var marker = Path.Combine(directory.FullName, "descendant.pid");
        var escapedMarker = marker.Replace("'", "''");
        var shell = WorkerShell.Executable.Replace("'", "''");
        var script = $"""
            $ErrorActionPreference = 'Stop'
            $child = Start-Process -FilePath '{shell}' -ArgumentList '-NoProfile -NonInteractive -Command Wait-Event' -NoNewWindow -PassThru
            [IO.File]::WriteAllText('{escapedMarker}.tmp', [string]$child.Id)
            [IO.File]::Move('{escapedMarker}.tmp', '{escapedMarker}')
            """;
        using var cancellation = new CancellationTokenSource();
        Process? descendant = null;
        SpawnProcessIdentity? descendantIdentity = null;
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => WorkerProcessRunner.RunBoundedDirectAsync(
                Request(script), 65536, cancellation.Token, new DeadlineTime(), async root =>
                {
                    // Wait at the launch boundary so a late attachment cannot capture the descendant.
                    await MarkerFileProbe.WaitAsync(marker, TestHangGuard.Bound);
                    descendant = Process.GetProcessById(int.Parse(await File.ReadAllTextAsync(marker)));
                    _ = descendant.SafeHandle;
                    Assert.True(SpawnProcessIdentityReader.TryRead(descendant, out var identity),
                        "Record the status probe descendant identity for cleanup.");
                    descendantIdentity = identity;
                    await TestHangGuard.CompletesWithinAsync(root.WaitForExitAsync(), TestHangGuard.Bound, "status probe root exit");
                    Assert.Equal(0, root.ExitCode);
                    Assert.False(descendant.HasExited);
                    cancellation.Cancel();
                }, null));
            Assert.NotNull(descendant);
            await TestHangGuard.CompletesWithinAsync(descendant.WaitForExitAsync(), TestHangGuard.Bound, "status probe descendant exit after cancellation");
            Assert.True(descendant.HasExited);
        }
        finally
        {
            using (descendant)
            {
                if (descendant is not null && descendantIdentity is not null)
                    TestOwnedProcessStop.StopRootIfSame(descendant, descendantIdentity, TestHangGuard.Bound);
            }
            SharedTestSupport.RemoveTempDirectory(directory.FullName);
        }
    }

    private static WorkerProcessDirectRunRequest Request(string script) => new(WorkerShell.Executable,
        [.. WorkerShell.BaseArguments(), script], Path.GetTempPath(), TimeSpan.FromSeconds(10), RequireOwnedProcessGroup: true);

    private sealed class DeadlineTime : TimeProvider
    {
        private TimerCallback? _callback;
        private object? _state;
        public TimeSpan DueTime { get; private set; }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            _callback = callback;
            _state = state;
            DueTime = dueTime;
            return new Timer();
        }
        internal void Expire() => (_callback ?? throw new InvalidOperationException("Deadline was not armed."))(_state);
        private sealed class Timer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
