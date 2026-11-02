using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: in-memory goals, unique state/event paths, and an injected clock.
public sealed class ProgramHealthDeliveredForReviewTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "program-health-review", Guid.NewGuid().ToString("N"));
    private readonly AgentOrchestratorKernel _kernel = new();
    private readonly OwnerConsoleTestClock _clock = new();
    private string State => Path.Combine(_root, ConductorProgramHealthMonitor.StateFileName);
    private string Log => Path.Combine(_root, "events.jsonl");

    [Fact]
    public void OwnerReviewAloneDoesNotRaiseNoLanding()
    {
        var goal = VerifiedGoal();
        Deliver(goal);
        var reads = new List<GoalId>();
        var monitor = Monitor(id => { reads.Add(id); return []; });
        monitor.EvaluateWithInvariants(_kernel.Goals, "main");
        var baseline = File.ReadAllText(State);
        _clock.Now = _clock.Now.AddMinutes(121);

        monitor.EvaluateWithInvariants(_kernel.Goals, "main");

        Assert.Equal(new[] { goal.Id, goal.Id }, reads);
        Assert.Empty(Events());
        Assert.Equal(baseline, File.ReadAllText(State));
        using var state = JsonDocument.Parse(baseline);
        Assert.Equal(JsonValueKind.Null, state.RootElement.GetProperty("activeKey").ValueKind);
        Assert.Equal(_clock.Now.AddMinutes(-121), state.RootElement.GetProperty("observedSince").GetDateTimeOffset());
    }

    [Fact]
    public void MixedGoalsAlertAndClearWithSeparateCounts()
    {
        Deliver(VerifiedGoal());
        VerifiedGoal();
        var monitor = Monitor();
        monitor.EvaluateWithInvariants(_kernel.Goals, "main");
        Assert.True(File.Exists(State));
        Assert.Empty(Events());
        _clock.Now = _clock.Now.AddMinutes(121);

        monitor.EvaluateWithInvariants(_kernel.Goals, "main");

        var alert = Assert.Single(Events());
        Assert.StartsWith("PROGRAM_HEALTH_ALERT conditions=no-landing topBlocker=verified-goals ", alert);
        Assert.Contains(" verifiedGoals=1 awaitingOwnerReview=1 diagnosis=", alert);
        monitor.EvaluateWithInvariants(_kernel.Goals, "landed");
        Assert.Equal(new[]
        {
            alert,
            "PROGRAM_HEALTH_CLEARED previous=no-landing|verified-goals minutesSinceLanding=0 currentReceipts=0 verifiedGoals=1 awaitingOwnerReview=1"
        }, Events());
    }

    [Theory]
    [InlineData("replaced")]
    [InlineData("cleared")]
    [InlineData("suffixed")]
    public void DeliveryWithoutCurrentHoldStillCountsAsVerified(string change)
    {
        var goal = VerifiedGoal();
        Deliver(goal);
        if (change == "cleared") Assert.True(_kernel.ClearGoalHold(goal.Id));
        else _kernel.ObserveGoalHold(goal.Id, "Verified", change == "suffixed"
            ? DeliveredForReviewHold.Text(goal.DeliveredForReview!) + "; later landing blocker"
            : "later landing blocker", _clock.Now, TimeSpan.FromHours(1));
        Assert.NotNull(goal.DeliveredForReview);
        Assert.Equal(GoalStatus.Verified, goal.Status);
        Assert.False(DeliveredForReviewHold.IsCurrent(goal));
        var monitor = Monitor();
        monitor.EvaluateWithInvariants(_kernel.Goals, "main");
        _clock.Now = _clock.Now.AddMinutes(121);

        monitor.EvaluateWithInvariants(_kernel.Goals, "main");

        var alert = Assert.Single(Events());
        Assert.StartsWith("PROGRAM_HEALTH_ALERT conditions=no-landing topBlocker=verified-goals ", alert);
        Assert.Contains(" verifiedGoals=1 diagnosis=", alert);
        Assert.DoesNotContain("awaitingOwnerReview=", alert);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HeldReceiptStillAlertsWithOrWithoutOwnerReview(bool ownerReview)
    {
        var goal = VerifiedGoal();
        if (ownerReview) Deliver(goal);
        var receipt = new ProgramHealthReceiptObservation("held", _clock.Now.AddMinutes(-31), "main", [goal.Id.Value]);
        var reads = new List<GoalId>();
        var monitor = Monitor(id => { reads.Add(id); return [receipt]; });

        monitor.EvaluateWithInvariants(_kernel.Goals, "main");

        Assert.Equal(new[] { goal.Id }, reads);
        var alert = Assert.Single(Events());
        Assert.StartsWith("PROGRAM_HEALTH_ALERT conditions=held-receipt topBlocker=held ", alert);
        Assert.Contains(" currentReceipts=1 overThreshold=1 ", alert);
        Assert.Contains(ownerReview ? " verifiedGoals=0 awaitingOwnerReview=1 diagnosis=" : " verifiedGoals=1 diagnosis=", alert);
        if (!ownerReview) Assert.DoesNotContain("awaitingOwnerReview=", alert);
    }

    [Fact]
    public void DeliveryClearsExistingAlertUsingCurrentOwnerReviewCount()
    {
        var goal = VerifiedGoal();
        var monitor = Monitor();
        monitor.EvaluateWithInvariants(_kernel.Goals, "main");
        _clock.Now = _clock.Now.AddMinutes(121);
        monitor.EvaluateWithInvariants(_kernel.Goals, "main");
        var alert = Assert.Single(Events());
        Assert.StartsWith("PROGRAM_HEALTH_ALERT conditions=no-landing ", alert);
        Assert.DoesNotContain("awaitingOwnerReview=", alert);
        Deliver(goal);

        monitor.EvaluateWithInvariants(_kernel.Goals, "main");

        Assert.Equal(new[]
        {
            alert,
            "PROGRAM_HEALTH_CLEARED previous=no-landing|verified-goals minutesSinceLanding=121 currentReceipts=0 verifiedGoals=0 awaitingOwnerReview=1"
        }, Events());
    }

    [Fact]
    public void ZeroOwnerReviewCountLeavesBothFormatsUnchanged()
    {
        var snapshot = new ProgramHealthSnapshot(_clock.Now, null, _clock.Now.AddMinutes(-121), "main", 1, []);
        var assessment = ProgramHealthEvaluator.Evaluate(snapshot, new());

        Assert.Equal(0, assessment.AwaitingOwnerReview);
        Assert.Equal("PROGRAM_HEALTH_ALERT conditions=no-landing topBlocker=verified-goals minutesSinceLanding=121 lastLanding=none currentReceipts=0 overThreshold=0 verifiedGoals=1 diagnosis=Nothing landed for 2 h 1 min; 1 Verified goal and 0 passed trains waiting.",
            ProgramHealthEvaluator.FormatAlert(snapshot, assessment));
        Assert.Equal("PROGRAM_HEALTH_CLEARED previous=no-landing|verified-goals minutesSinceLanding=121 currentReceipts=0 verifiedGoals=1",
            ProgramHealthEvaluator.FormatCleared(assessment.Key, assessment));
    }

    private Goal VerifiedGoal()
    {
        var goal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(_kernel, AgentCatalog.Default().Agents, "Program health review");
        var task = Assert.Single(goal.Tasks);
        _kernel.RecordTaskDispatch(goal.Id, task.Id, new("test-worker", "test.exe", _root, _clock.Now));
        _kernel.RecordTaskVerification(goal.Id, task.Id, new("test.exe", _root, 0, "ok", "", _clock.Now));
        Assert.Equal(GoalStatus.Verified, goal.Status);
        return goal;
    }

    private void Deliver(Goal goal)
    {
        _kernel.RecordGoalDeliveredForReview(goal.Id, new GoalDeliveredForReview(16,
            "https://github.com/example/project/pull/16", new string('a', 40), "attempt", _clock.Now, "touchproof[bot]"));
        Assert.True(DeliveredForReviewHold.IsCurrent(goal));
    }

    private ConductorProgramHealthMonitor Monitor(Func<GoalId, IReadOnlyList<ProgramHealthReceiptObservation>>? reader = null) =>
        new(State, () => null, new ConductEventLogWriter(Log), _clock, reader ?? (_ => []));

    private string[] Events() => File.Exists(Log) ? File.ReadAllLines(Log).Select(line =>
    {
        using var document = JsonDocument.Parse(line);
        Assert.Equal("program-health", document.RootElement.GetProperty("eventKind").GetString());
        return document.RootElement.GetProperty("detail").GetString()!;
    }).ToArray() : [];

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
