using Xunit.Sdk;

// Parallel-safe: the core tests use only injected time and in-memory observations.
public sealed class OwnedAttemptExitWaitTests
{
    private const string Trail = "phase=owner-persisted utc=2026-10-25T00:00:00.0000000Z pid=42\n";
    private static readonly OwnedAttemptExitWait.Observation Fixture =
        new(Trail.Length, 11, 11, null, Trail, "HB-FIXTURE", "OUT-FIXTURE", "ERR-FIXTURE");

    [Fact]
    public void NoProgress_FailsAtWindowWithLastPhaseAndAllDiagnostics()
    {
        var clock = new Clock();
        var error = Assert.ThrowsAny<XunitException>(() => OwnedAttemptExitWait.WaitForExit(
            42, () => false, () => Fixture, () => clock.Elapsed, clock.Sleep));

        Assert.Equal(TestHangGuard.HangSafetyBound, clock.Elapsed);
        Assert.StartsWith("External acceptance PID 42 made no progress for 120 s", error.Message);
        Assert.Contains("lastPhase=owner-persisted", error.Message);
        foreach (var text in new[] { Trail, "HB-FIXTURE", "OUT-FIXTURE", "ERR-FIXTURE" })
            Assert.Contains(text, error.Message);
    }

    [Fact]
    public void TrailGrowsEveryMinute_ChildExitsAfterOriginalWindow()
    {
        var clock = new Clock();
        OwnedAttemptExitWait.WaitForExit(42, () => clock.Elapsed >= TimeSpan.FromSeconds(250),
            () => Fixture with { TrailLength = (long)clock.Elapsed.TotalSeconds / 60 },
            () => clock.Elapsed, clock.Sleep);

        Assert.Equal(TimeSpan.FromSeconds(250), clock.Elapsed);
        Assert.True(clock.Sleeps > 0);
    }

    [Fact]
    public void ContinuousProgress_WaitsBeyondFormerTotalBoundUntilExit()
    {
        var clock = new Clock();
        OwnedAttemptExitWait.WaitForExit(
            42, () => clock.Elapsed >= TimeSpan.FromSeconds(900),
            () => Fixture with { TrailLength = (long)clock.Elapsed.TotalSeconds },
            () => clock.Elapsed, clock.Sleep);

        Assert.Equal(TimeSpan.FromSeconds(900), clock.Elapsed);
    }

    [Fact]
    public void ProgressBeyondFormerTotalBoundThenFreeze_FailsOneWindowAfterLastChange()
    {
        var clock = new Clock();
        var lastChange = TimeSpan.FromSeconds(360);
        var error = Assert.ThrowsAny<XunitException>(() => OwnedAttemptExitWait.WaitForExit(
            42, () => false,
            () => Fixture with { TrailLength = (long)Math.Min(clock.Elapsed.TotalSeconds, lastChange.TotalSeconds) },
            () => clock.Elapsed, clock.Sleep));

        Assert.Equal(lastChange + TestHangGuard.HangSafetyBound, clock.Elapsed);
        Assert.StartsWith("External acceptance PID 42 made no progress for 120 s", error.Message);
        Assert.Contains("lastPhase=owner-persisted", error.Message);
        foreach (var text in new[] { Trail, "HB-FIXTURE", "OUT-FIXTURE", "ERR-FIXTURE" })
            Assert.Contains(text, error.Message);
    }

    [Fact]
    public void AlreadyExited_ReturnsWithoutObservingOrSleeping()
    {
        var clock = new Clock();
        var exitChecks = 0;
        OwnedAttemptExitWait.WaitForExit(42, () => { exitChecks++; return true; },
            () => throw new InvalidOperationException("Exited children need no observation."),
            () => clock.Elapsed, clock.Sleep);

        Assert.Equal(1, exitChecks);
        Assert.Equal(0, clock.Sleeps);
        Assert.Equal(TimeSpan.Zero, clock.Elapsed);
    }

    [Theory]
    [InlineData("trail")]
    [InlineData("stdout")]
    [InlineData("stderr")]
    [InlineData("heartbeat")]
    public void AnyMeasurementChanges_ResetsNoProgressWindow(string measurement)
    {
        var clock = new Clock();
        OwnedAttemptExitWait.Observation Observe()
        {
            // Lengths decrease as well as increase; any change is progress.
            var step = (long)clock.Elapsed.TotalSeconds / 60 % 2;
            return measurement switch
            {
                "trail" => Fixture with { TrailLength = step },
                "stdout" => Fixture with { StdoutLength = step },
                "stderr" => Fixture with { StderrLength = step },
                _ => Fixture with { HeartbeatWriteUtc = DateTime.UnixEpoch.AddSeconds(step) }
            };
        }
        OwnedAttemptExitWait.WaitForExit(42, () => clock.Elapsed >= TimeSpan.FromSeconds(250),
            Observe, () => clock.Elapsed, clock.Sleep);

        Assert.Equal(TimeSpan.FromSeconds(250), clock.Elapsed);
    }

    [Fact]
    public void DiagnosticTextChanges_WithoutMeasurementChangeStillStalls()
    {
        var clock = new Clock();
        var error = Assert.ThrowsAny<XunitException>(() => OwnedAttemptExitWait.WaitForExit(
            42, () => false, () => Fixture with { Stdout = $"output-{clock.Sleeps}" },
            () => clock.Elapsed, clock.Sleep));

        Assert.Equal(TestHangGuard.HangSafetyBound, clock.Elapsed);
        Assert.Contains("made no progress", error.Message);
        Assert.Contains($"stdout=output-{clock.Sleeps}", error.Message);
    }

    [Fact]
    public void ExitAtNoProgressBoundary_ReturnsBeforeStallDecision()
    {
        var clock = new Clock();
        var window = TestHangGuard.HangSafetyBound;
        OwnedAttemptExitWait.WaitForExit(42, () => clock.Elapsed >= window,
            () => Fixture, () => clock.Elapsed, clock.Sleep);

        Assert.Equal(window, clock.Elapsed);
    }

    private sealed class Clock
    {
        internal TimeSpan Elapsed { get; private set; }
        internal int Sleeps { get; private set; }
        internal void Sleep(TimeSpan duration)
        {
            Assert.True(duration > TimeSpan.Zero);
            Sleeps++;
            Elapsed += duration;
        }
    }
}
