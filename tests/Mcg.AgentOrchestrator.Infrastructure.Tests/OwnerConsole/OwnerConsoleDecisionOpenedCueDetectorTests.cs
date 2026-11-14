using Mcg.AgentOrchestrator.App.OwnerConsole;

// Parallel-safe: per-test detectors and explicit model timestamps, without IO.
public sealed class OwnerConsoleDecisionOpenedCueDetectorTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Opening_OneOrSeveralIds_EmitsDecisionExactlyOnce(int count)
    {
        var detector = new OwnerConsoleNotificationDetector(new());
        var previous = Model();
        detector.Detect(null, previous, Now, OwnerConsoleBellTiers.All);
        var current = Model(Enumerable.Range(0, count).Select(id => Decision(id.ToString())).ToArray());

        Assert.Equal([OwnerConsoleNotificationTier.Decision],
            detector.Detect(previous, current, Now, OwnerConsoleBellTiers.All));
        Assert.Empty(detector.Detect(current, current, Now.AddSeconds(1), OwnerConsoleBellTiers.All));
    }

    [Fact]
    public void Startup_WithExistingDecision_EmitsNothingAndDoesNotReplay()
    {
        var detector = new OwnerConsoleNotificationDetector(new());
        var current = Model(Decision("existing"));

        Assert.Empty(detector.Detect(null, current, Now, OwnerConsoleBellTiers.All));
        Assert.Empty(detector.Detect(current, current, Now.AddSeconds(1), OwnerConsoleBellTiers.All));
    }

    [Theory]
    [InlineData("decisions-loading")]
    [InlineData("decisions-error")]
    [InlineData("activity-loading")]
    [InlineData("activity-error")]
    public void Baseline_UnreadyAndRecovery_ConsumeIdsWithoutCatchUp(string state)
    {
        var detector = new OwnerConsoleNotificationDetector(new());
        var previous = Model();
        detector.Detect(null, previous, Now, OwnerConsoleBellTiers.All);
        var current = Model(Decision("during-baseline"));
        var unready = state switch
        {
            "decisions-loading" => current with { DecisionsState = new(Loading: true) },
            "decisions-error" => current with { DecisionsState = new(Error: "unavailable") },
            "activity-loading" => current with { ActivityState = new(Loading: true) },
            _ => current with { ActivityState = new(Error: "unavailable") }
        };
        Assert.Empty(detector.Detect(previous, unready, Now, OwnerConsoleBellTiers.All));
        var recovered = Model(Decision("during-baseline"), Decision("during-recovery"));
        Assert.Empty(detector.Detect(unready, recovered, Now.AddSeconds(1), OwnerConsoleBellTiers.All));
        Assert.Empty(detector.Detect(recovered, recovered, Now.AddSeconds(2), OwnerConsoleBellTiers.All));
    }

    [Fact]
    public void MutedOpening_IsConsumed_AndStillStartsAttentionTracking()
    {
        var detector = new OwnerConsoleNotificationDetector(new());
        var previous = Model();
        detector.Detect(null, previous, Now, OwnerConsoleBellTiers.All);
        var current = Model(Decision("muted"));
        var muted = OwnerConsoleBellTiers.All.With(OwnerConsoleNotificationTier.Decision, false);

        Assert.Empty(detector.Detect(previous, current, Now, muted));
        Assert.Empty(detector.Detect(current, current, Now.AddMinutes(1), OwnerConsoleBellTiers.All));
        Assert.Equal([OwnerConsoleNotificationTier.Attention],
            detector.Detect(current, current, Now.AddMinutes(30), OwnerConsoleBellTiers.All));
    }

    [Fact]
    public void Waiting_AfterOpening_EmitsAttentionOnlyAtThreshold()
    {
        var detector = new OwnerConsoleNotificationDetector(new() { AttentionThreshold = TimeSpan.FromMinutes(5) });
        var previous = Model();
        detector.Detect(null, previous, Now, OwnerConsoleBellTiers.All);
        var current = Model(Decision("waiting"));

        Assert.Equal([OwnerConsoleNotificationTier.Decision],
            detector.Detect(previous, current, Now, OwnerConsoleBellTiers.All));
        Assert.Empty(detector.Detect(current, current, Now.AddMinutes(4), OwnerConsoleBellTiers.All));
        Assert.Equal([OwnerConsoleNotificationTier.Attention],
            detector.Detect(current, current, Now.AddMinutes(5), OwnerConsoleBellTiers.All));
        Assert.Empty(detector.Detect(current, current, Now.AddMinutes(6), OwnerConsoleBellTiers.All));
    }

    [Fact]
    public void ReappearingId_AfterAbsence_CuesAgainButContentEditsDoNot()
    {
        var detector = new OwnerConsoleNotificationDetector(new());
        var empty = Model();
        var current = Model(Decision("returning"));
        detector.Detect(null, current, Now, OwnerConsoleBellTiers.All);
        var edited = Model(Decision("returning") with { Summary = "Changed summary" });
        Assert.Empty(detector.Detect(current, edited, Now.AddSeconds(1), OwnerConsoleBellTiers.All));
        Assert.Empty(detector.Detect(edited, empty, Now.AddSeconds(2), OwnerConsoleBellTiers.All));
        Assert.Equal([OwnerConsoleNotificationTier.Decision],
            detector.Detect(empty, current, Now.AddSeconds(3), OwnerConsoleBellTiers.All));
    }

    [Fact]
    public void SimultaneousNotifications_AppendDecisionAfterExistingTiers()
    {
        var detector = new OwnerConsoleNotificationDetector(new());
        var previous = Model(Decision("waiting") with { RaisedAt = Now.AddMinutes(-29) });
        previous = previous with { Status = previous.Status with { ConductorRunning = ConductorLiveness.Running } };
        detector.Detect(null, previous, Now, OwnerConsoleBellTiers.All);
        var stopped = previous with { Status = previous.Status with { ConductorRunning = ConductorLiveness.NotRunning } };
        Assert.Empty(detector.Detect(previous, stopped, Now.AddSeconds(1), OwnerConsoleBellTiers.All));
        var current = stopped with
        {
            Status = stopped.Status with { LandedToday = 1 },
            Decisions = [previous.Decisions[0], Decision("new")]
        };

        Assert.Equal([OwnerConsoleNotificationTier.Calm, OwnerConsoleNotificationTier.Attention,
                OwnerConsoleNotificationTier.Alarm, OwnerConsoleNotificationTier.Decision],
            detector.Detect(stopped, current, Now.AddMinutes(2), OwnerConsoleBellTiers.All));
    }

    private static OwnerConsoleDecision Decision(string id) =>
        new(id, 1, "goal", "goal", OwnerQuestionKind.HumanInput, "Answer?", "Answer?", null, null, null, Now);
    private static OwnerConsoleViewModel Model(params OwnerConsoleDecision[] decisions) =>
        new(new(ConductorLiveness.NotRunning, 0, decisions.Length, 0, null, 0), [.. decisions], [], []);
}
