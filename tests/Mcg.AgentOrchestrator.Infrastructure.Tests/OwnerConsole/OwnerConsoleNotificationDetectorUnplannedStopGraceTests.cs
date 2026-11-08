using Mcg.AgentOrchestrator.App.OwnerConsole;

// Parallel-safe: injected times and models are local; file cases own unique temporary directories.
public sealed class OwnerConsoleNotificationDetectorUnplannedStopGraceTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void LateHandoffReasonDropsPendingStopAlarm()
    {
        var detector = new OwnerConsoleNotificationDetector(new());
        var running = Model(true);
        var stopped = Model(false);
        var handoff = Model(false, "self-relaunch-handoff", Now);
        Assert.Empty(detector.Detect(null, running, Now, OwnerConsoleBellTiers.All));
        Assert.Empty(detector.Detect(running, stopped, Now, OwnerConsoleBellTiers.All));
        Assert.Empty(detector.Detect(stopped, handoff, Now.AddSeconds(10), OwnerConsoleBellTiers.All));
        Assert.Empty(detector.Detect(handoff, handoff, Now.AddMinutes(2), OwnerConsoleBellTiers.All));
    }

    [Fact]
    public void RecoveryWithinGraceDropsPendingStopAlarm()
    {
        var detector = new OwnerConsoleNotificationDetector(new());
        var running = Model(true);
        var stopped = Model(false);
        Assert.Empty(detector.Detect(null, running, Now, OwnerConsoleBellTiers.All));
        Assert.Empty(detector.Detect(running, stopped, Now, OwnerConsoleBellTiers.All));
        Assert.Equal([OwnerConsoleNotificationTier.Calm],
            detector.Detect(stopped, running, Now.AddSeconds(30), OwnerConsoleBellTiers.All));
        Assert.Empty(detector.Detect(running, running, Now.AddMinutes(2), OwnerConsoleBellTiers.All));
    }

    [Fact]
    public void UnplannedStopRingsOnceAtGraceDeadline()
    {
        var detector = new OwnerConsoleNotificationDetector(new());
        var running = Model(true);
        var stopped = Model(false);
        Assert.Empty(detector.Detect(null, running, Now, OwnerConsoleBellTiers.All));
        Assert.Empty(detector.Detect(running, stopped, Now, OwnerConsoleBellTiers.All));
        Assert.Empty(detector.Detect(stopped, stopped, Now.AddSeconds(30), OwnerConsoleBellTiers.All));
        Assert.Empty(detector.Detect(stopped, stopped, Now.AddSeconds(59), OwnerConsoleBellTiers.All));
        Assert.Equal([OwnerConsoleNotificationTier.Alarm],
            detector.Detect(stopped, stopped, Now.AddSeconds(60), OwnerConsoleBellTiers.All));
        Assert.Empty(detector.Detect(stopped, stopped, Now.AddSeconds(61), OwnerConsoleBellTiers.All));
        Assert.Empty(detector.Detect(stopped, stopped, Now.AddMinutes(5), OwnerConsoleBellTiers.All));
    }

    [Fact]
    public void LateHandoffThatStaysDownRingsOnceAfterStaleThreshold()
    {
        var detector = new OwnerConsoleNotificationDetector(new() { StaleTickThreshold = TimeSpan.FromMinutes(5) });
        var running = Model(true);
        var stopped = Model(false);
        var handoff = Model(false, "self-relaunch-handoff", Now);
        Assert.Empty(detector.Detect(null, running, Now, OwnerConsoleBellTiers.All));
        Assert.Empty(detector.Detect(running, stopped, Now, OwnerConsoleBellTiers.All));
        Assert.Empty(detector.Detect(stopped, handoff, Now.AddSeconds(10), OwnerConsoleBellTiers.All));
        Assert.Empty(detector.Detect(handoff, handoff, Now.AddMinutes(5), OwnerConsoleBellTiers.All));
        Assert.Equal([OwnerConsoleNotificationTier.Alarm],
            detector.Detect(handoff, handoff, Now.AddMinutes(5).AddTicks(1), OwnerConsoleBellTiers.All));
        Assert.Empty(detector.Detect(handoff, handoff, Now.AddMinutes(7), OwnerConsoleBellTiers.All));
    }

    [Theory]
    [InlineData(false, 60)]
    [InlineData(false, 120)]
    [InlineData(true, 60)]
    [InlineData(true, 120)]
    public void RecoveryOrPlannedReasonAtDeadlineCancelsPendingAlarm(bool recovered, int seconds)
    {
        var detector = new OwnerConsoleNotificationDetector(new());
        var running = Model(true);
        var stopped = Model(false);
        var resolved = recovered ? running : Model(false, "stop-file", Now);
        Assert.Empty(detector.Detect(null, running, Now, OwnerConsoleBellTiers.All));
        Assert.Empty(detector.Detect(running, stopped, Now, OwnerConsoleBellTiers.All));
        Assert.Equal(recovered ? [OwnerConsoleNotificationTier.Calm] : Array.Empty<OwnerConsoleNotificationTier>(),
            detector.Detect(stopped, resolved, Now.AddSeconds(seconds), OwnerConsoleBellTiers.All));
        Assert.Empty(detector.Detect(resolved, resolved, Now.AddMinutes(3), OwnerConsoleBellTiers.All));
    }

    [Fact]
    public void LateHandoffAlreadyPastStaleThresholdRingsImmediatelyOnce()
    {
        var detector = new OwnerConsoleNotificationDetector(new() { StaleTickThreshold = TimeSpan.FromMinutes(5) });
        var running = Model(true);
        var stopped = Model(false);
        var handoff = Model(false, "self-relaunch-handoff", Now.AddMinutes(-5));
        Assert.Empty(detector.Detect(null, running, Now, OwnerConsoleBellTiers.All));
        Assert.Empty(detector.Detect(running, stopped, Now, OwnerConsoleBellTiers.All));
        Assert.Equal([OwnerConsoleNotificationTier.Alarm],
            detector.Detect(stopped, handoff, Now.AddSeconds(10), OwnerConsoleBellTiers.All));
        Assert.Empty(detector.Detect(handoff, handoff, Now.AddMinutes(2), OwnerConsoleBellTiers.All));
    }

    [Fact]
    public void MutedGraceDeadlineIsConsumedAndNextCrossingRearms()
    {
        var detector = new OwnerConsoleNotificationDetector(new());
        var running = Model(true);
        var stopped = Model(false);
        Assert.Empty(detector.Detect(null, running, Now, OwnerConsoleBellTiers.All));
        Assert.Empty(detector.Detect(running, stopped, Now, OwnerConsoleBellTiers.All));
        Assert.Empty(detector.Detect(stopped, stopped, Now.AddMinutes(1), OwnerConsoleBellTiers.All with { Alarm = false }));
        Assert.Empty(detector.Detect(stopped, stopped, Now.AddMinutes(2), OwnerConsoleBellTiers.All));
        Assert.Equal([OwnerConsoleNotificationTier.Calm],
            detector.Detect(stopped, running, Now.AddMinutes(3), OwnerConsoleBellTiers.All));
        Assert.Empty(detector.Detect(running, stopped, Now.AddMinutes(4), OwnerConsoleBellTiers.All));
        Assert.Empty(detector.Detect(stopped, stopped, Now.AddMinutes(4).AddSeconds(59), OwnerConsoleBellTiers.All));
        Assert.Equal([OwnerConsoleNotificationTier.Alarm],
            detector.Detect(stopped, stopped, Now.AddMinutes(5), OwnerConsoleBellTiers.All));
        Assert.Empty(detector.Detect(stopped, stopped, Now.AddMinutes(6), OwnerConsoleBellTiers.All));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"unplannedStopGraceSeconds\":\"bad\"}")]
    [InlineData("{\"unplannedStopGraceSeconds\":null}")]
    [InlineData("{\"unplannedStopGraceSeconds\":0}")]
    [InlineData("{\"unplannedStopGraceSeconds\":-1}")]
    [InlineData("{\"unplannedStopGraceSeconds\":1e300}")]
    [InlineData("{\"unplannedStopGraceSeconds\":1e400}")]
    [InlineData("{\"unplannedStopGraceSeconds\":1e-300}")]
    public void GraceSettingFallsBackToSixtySeconds(string text)
    {
        WithFile(text, path => Assert.Equal(TimeSpan.FromSeconds(60),
            OwnerConsoleNotificationSettings.Load(path).UnplannedStopGrace));
    }

    [Fact]
    public void GraceSettingReadsSecondsCaseInsensitively()
    {
        Assert.Equal(TimeSpan.FromSeconds(60), new OwnerConsoleNotificationSettings().UnplannedStopGrace);
        WithFile("""{"UnplannedStopGraceSeconds":10.5}""", path =>
            Assert.Equal(TimeSpan.FromSeconds(10.5), OwnerConsoleNotificationSettings.Load(path).UnplannedStopGrace));
    }

    [Fact]
    public void ConfiguredGraceFromSettingsFileDrivesDetector()
    {
        WithFile("""{"unplannedStopGraceSeconds":10}""", path =>
        {
            var detector = new OwnerConsoleNotificationDetector(OwnerConsoleNotificationSettings.Load(path));
            var running = Model(true);
            var stopped = Model(false);
            Assert.Empty(detector.Detect(null, running, Now, OwnerConsoleBellTiers.All));
            Assert.Empty(detector.Detect(running, stopped, Now, OwnerConsoleBellTiers.All));
            Assert.Empty(detector.Detect(stopped, stopped, Now.AddSeconds(9), OwnerConsoleBellTiers.All));
            Assert.Equal([OwnerConsoleNotificationTier.Alarm],
                detector.Detect(stopped, stopped, Now.AddSeconds(10), OwnerConsoleBellTiers.All));
            Assert.Empty(detector.Detect(stopped, stopped, Now.AddSeconds(11), OwnerConsoleBellTiers.All));
        });
    }

    private static OwnerConsoleViewModel Model(bool running, string? reason = null, DateTimeOffset? at = null) =>
        new(new(running, 0, 0, 0, null, 0, PlannedStopReason: reason, PlannedStopAt: at), [], [], []);

    private static void WithFile(string text, Action<string> assertion)
    {
        var root = SharedTestSupport.CreateTempDirectory();
        try
        {
            var path = Path.Combine(root, "settings.json");
            File.WriteAllText(path, text);
            assertion(path);
        }
        finally { SharedTestSupport.RemoveTempDirectory(root); }
    }
}
