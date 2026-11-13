using Mcg.AgentOrchestrator.App.OwnerConsole;

// Parallel-safe: detector state, models and supplied clock values belong to each test.
public sealed class OwnerConsoleNotificationDetectorLivenessTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void RunningUnknownRunning_WithFreshTick_RemainsSilent()
    {
        var detector = new OwnerConsoleNotificationDetector(new());
        var running = Model(ConductorLiveness.Running, Now);
        var unknown = Model(ConductorLiveness.Unknown, Now);
        Assert.Empty(detector.Detect(null, running, Now, OwnerConsoleBellTiers.All));
        Assert.Empty(detector.Detect(running, unknown, Now.AddSeconds(10), OwnerConsoleBellTiers.All));
        Assert.Empty(detector.Detect(unknown, unknown, Now.AddSeconds(75), OwnerConsoleBellTiers.All));
        Assert.Empty(detector.Detect(unknown, running, Now.AddSeconds(90), OwnerConsoleBellTiers.All));
    }

    [Fact]
    public void UnknownBeforeStop_GraceStartsOnlyAtConfirmedStop()
    {
        var detector = new OwnerConsoleNotificationDetector(new());
        var running = Model(ConductorLiveness.Running);
        var unknown = Model(ConductorLiveness.Unknown);
        var stopped = Model(ConductorLiveness.NotRunning);
        Assert.Empty(detector.Detect(null, running, Now, OwnerConsoleBellTiers.All));
        Assert.Empty(detector.Detect(running, unknown, Now.AddSeconds(10), OwnerConsoleBellTiers.All));
        Assert.Empty(detector.Detect(unknown, unknown, Now.AddSeconds(75), OwnerConsoleBellTiers.All));
        Assert.Empty(detector.Detect(unknown, stopped, Now.AddSeconds(90), OwnerConsoleBellTiers.All));
        Assert.Empty(detector.Detect(stopped, stopped, Now.AddSeconds(149), OwnerConsoleBellTiers.All));
        Assert.Equal([OwnerConsoleNotificationTier.Alarm],
            detector.Detect(stopped, stopped, Now.AddSeconds(150), OwnerConsoleBellTiers.All));
        Assert.Empty(detector.Detect(stopped, stopped, Now.AddSeconds(180), OwnerConsoleBellTiers.All));
    }

    [Fact]
    public void UnknownDuringStop_GraceDeadlineIsPreservedAndConsumedOnce()
    {
        var detector = new OwnerConsoleNotificationDetector(new());
        var running = Model(ConductorLiveness.Running);
        var stopped = Model(ConductorLiveness.NotRunning);
        var unknown = Model(ConductorLiveness.Unknown);
        Assert.Empty(detector.Detect(null, running, Now, OwnerConsoleBellTiers.All));
        Assert.Empty(detector.Detect(running, stopped, Now, OwnerConsoleBellTiers.All));
        Assert.Empty(detector.Detect(stopped, unknown, Now.AddSeconds(30), OwnerConsoleBellTiers.All));
        Assert.Empty(detector.Detect(unknown, unknown, Now.AddSeconds(59), OwnerConsoleBellTiers.All));
        Assert.Equal([OwnerConsoleNotificationTier.Alarm],
            detector.Detect(unknown, unknown, Now.AddSeconds(60), OwnerConsoleBellTiers.All));
        Assert.Empty(detector.Detect(unknown, stopped, Now.AddSeconds(61), OwnerConsoleBellTiers.All));
        Assert.Empty(detector.Detect(stopped, stopped, Now.AddMinutes(2), OwnerConsoleBellTiers.All));
    }

    [Fact]
    public void UnknownWhileRunning_StaleTickCrossingKeepsItsOriginalDeadline()
    {
        var detector = new OwnerConsoleNotificationDetector(new() { StaleTickThreshold = TimeSpan.FromMinutes(5) });
        var running = Model(ConductorLiveness.Running, Now);
        var unknown = Model(ConductorLiveness.Unknown, Now);
        Assert.Empty(detector.Detect(null, running, Now, OwnerConsoleBellTiers.All));
        Assert.Empty(detector.Detect(running, unknown, Now.AddMinutes(4), OwnerConsoleBellTiers.All));
        Assert.Empty(detector.Detect(unknown, unknown, Now.AddMinutes(5), OwnerConsoleBellTiers.All));
        Assert.Equal([OwnerConsoleNotificationTier.Alarm],
            detector.Detect(unknown, unknown, Now.AddMinutes(5).AddTicks(1), OwnerConsoleBellTiers.All));
        Assert.Empty(detector.Detect(unknown, running, Now.AddMinutes(6), OwnerConsoleBellTiers.All));
        Assert.Empty(detector.Detect(running, running, Now.AddMinutes(7), OwnerConsoleBellTiers.All));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void InitialUnknown_WaitsForKnownReadingWithoutInventingTransition(
        bool firstKnownRunning)
    {
        var detector = new OwnerConsoleNotificationDetector(new());
        var unknown = Model(ConductorLiveness.Unknown, Now.AddHours(-1));
        var known = Model(firstKnownRunning ? ConductorLiveness.Running : ConductorLiveness.NotRunning,
            Now.AddHours(-1));
        Assert.Empty(detector.Detect(null, unknown, Now, OwnerConsoleBellTiers.All));
        Assert.Empty(detector.Detect(unknown, unknown, Now.AddMinutes(20), OwnerConsoleBellTiers.All));
        Assert.Empty(detector.Detect(unknown, known, Now.AddMinutes(21), OwnerConsoleBellTiers.All));
        Assert.Empty(detector.Detect(known, known, Now.AddMinutes(22), OwnerConsoleBellTiers.All));
        Assert.Empty(detector.Detect(known, known, Now.AddMinutes(37), OwnerConsoleBellTiers.All));
    }

    [Fact]
    public void UnknownAfterKnownStop_StaleTickWaitsForActualRestart()
    {
        var detector = new OwnerConsoleNotificationDetector(new() { StaleTickThreshold = TimeSpan.FromMinutes(5) });
        var stopped = Model(ConductorLiveness.NotRunning, Now.AddHours(-1));
        var unknown = Model(ConductorLiveness.Unknown, Now.AddHours(-1));
        var running = Model(ConductorLiveness.Running, Now.AddHours(-1));
        Assert.Empty(detector.Detect(null, stopped, Now, OwnerConsoleBellTiers.All));
        Assert.Empty(detector.Detect(stopped, unknown, Now.AddMinutes(20), OwnerConsoleBellTiers.All));
        Assert.Empty(detector.Detect(unknown, unknown, Now.AddMinutes(21), OwnerConsoleBellTiers.All));
        Assert.Empty(detector.Detect(unknown, running, Now.AddMinutes(22), OwnerConsoleBellTiers.All));
        Assert.Empty(detector.Detect(running, running, Now.AddMinutes(27), OwnerConsoleBellTiers.All));
        Assert.Equal([OwnerConsoleNotificationTier.Alarm],
            detector.Detect(running, running, Now.AddMinutes(27).AddTicks(1), OwnerConsoleBellTiers.All));
    }

    [Fact]
    public void LandingThenRelaunchTwoMinutesLater_OnlyLandingPlaysCalm()
    {
        var detector = new OwnerConsoleNotificationDetector(new());
        var running = Model(ConductorLiveness.Running, landed: 3);
        var landed = Model(ConductorLiveness.Running, landed: 4);
        var stopped = landed with { Status = landed.Status with
            { ConductorRunning = ConductorLiveness.NotRunning, PlannedStopReason = "self-relaunch-handoff", PlannedStopAt = Now } };
        Assert.Empty(detector.Detect(null, running, Now, OwnerConsoleBellTiers.All));
        Assert.Equal([OwnerConsoleNotificationTier.Calm],
            detector.Detect(running, landed, Now, OwnerConsoleBellTiers.All));
        Assert.Empty(detector.Detect(landed, landed, Now.AddSeconds(10), OwnerConsoleBellTiers.All));
        Assert.Empty(detector.Detect(landed, stopped, Now.AddSeconds(30), OwnerConsoleBellTiers.All));
        Assert.Empty(detector.Detect(stopped, landed, Now.AddMinutes(2), OwnerConsoleBellTiers.All));
        Assert.Empty(detector.Detect(landed, landed, Now.AddMinutes(3), OwnerConsoleBellTiers.All));
    }

    private static OwnerConsoleViewModel Model(ConductorLiveness liveness,
        DateTimeOffset? tick = null, int landed = 0) =>
        new(new(liveness, 0, 0, 0, null, landed, LatestTickAt: tick), [], [], []);
}
