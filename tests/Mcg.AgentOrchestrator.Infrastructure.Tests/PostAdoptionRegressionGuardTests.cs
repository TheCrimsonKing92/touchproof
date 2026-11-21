using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using GoalDecision = Mcg.AgentOrchestrator.App.Orchestration.PostAdoptionRegressionEvaluator.GoalDecision;
using HandleStatus = Mcg.AgentOrchestrator.App.Orchestration.PostAdoptionRegressionReverter.HandleStatus;

public sealed class PostAdoptionRegressionGuardTests
{
    private const string Landing = "landing-one";
    private static readonly DateTimeOffset AdoptedAt = new(2026, 11, 21, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void TwoEligibleGoalsHeldWithOneNewReasonRevertTheAdoptedLandingOnce()
    {
        var fixture = new Incident();
        var guard = fixture.CreateGuard(Landing, Eligible("one", "two"));
        var hold = Hold("shared blocker");

        guard.ObserveGoalResult(1, AdoptedAt.AddMinutes(1), "one", hold, false, false, 2);
        guard.ObserveGoalResult(1, AdoptedAt.AddMinutes(1), "two", hold, false, false, 2);
        Assert.Empty(fixture.Reverts);
        Assert.Empty(fixture.Events);

        guard.ObserveGoalResult(2, AdoptedAt.AddMinutes(2), "one", hold, false, false, 2);
        guard.ObserveGoalResult(2, AdoptedAt.AddMinutes(2), "two", hold, false, false, 2);
        guard.ObserveGoalResult(3, AdoptedAt.AddMinutes(3), "one", hold, false, false, 2);

        Assert.Equal(Landing, Assert.Single(fixture.Reverts));
        var conductEvent = Assert.Single(fixture.Events);
        Assert.StartsWith($"POST_ADOPTION_REGRESSION_REVERTED landing={Landing} ", conductEvent);
        Assert.Contains("trigger=shared-blocker ", conductEvent);
        Assert.Contains("\"GoalId\":\"one\"", conductEvent);
        Assert.Contains("\"GoalId\":\"two\"", conductEvent);
    }

    [Fact]
    public void SingleGoalSpecificHoldWithBusyGateSlotDoesNotRevert()
    {
        var fixture = new Incident();
        var guard = fixture.CreateGuard(Landing, Eligible("one", "two"));

        var status = guard.ObserveTick([Held("one", "goal-specific blocker")],
            gateRunning: false, gateSlotFree: false, verifiedGoalCount: 1, AdoptedAt.AddMinutes(1));

        Assert.Equal(HandleStatus.NotRegression, status);
        Assert.Empty(fixture.Reverts);
        Assert.Empty(fixture.Events);
    }

    [Fact]
    public void SecondRegressingLandingInTheSameIncidentIsSuppressed()
    {
        var fixture = new Incident();
        var guard = fixture.CreateGuard(Landing, Eligible("one", "two"));

        Assert.Equal(HandleStatus.Reverted, guard.ObserveTick(SharedHolds(), false, false, 2, AdoptedAt.AddMinutes(1)));
        var secondAdoption = AdoptedAt.AddHours(1);
        guard.Adopt("landing-two", Eligible("one", "two"), secondAdoption);
        Assert.Equal(HandleStatus.Suppressed, guard.ObserveTick(SharedHolds(), false, false, 2, secondAdoption.AddMinutes(1)));

        Assert.Equal(Landing, Assert.Single(fixture.Reverts));
        Assert.Single(fixture.Events);
    }

    [Theory]
    [InlineData(true, 1, 1)]
    [InlineData(false, 11, 1)]
    [InlineData(false, 1, 31)]
    public void RunningGateOrElapsedTickWindowDoesNotRevert(bool gateRunning, int tick, int elapsedMinutes)
    {
        var fixture = new Incident();
        var guard = fixture.CreateGuard(Landing, Eligible("one", "two"));
        var now = AdoptedAt.AddMinutes(elapsedMinutes);
        for (var priorTick = 1; priorTick < tick; priorTick++)
            guard.ObserveTick([], false, false, 0, now);

        Assert.Equal(HandleStatus.NotRegression, guard.ObserveTick(SharedHolds(), gateRunning, true, 2, now));
        Assert.Empty(fixture.Reverts);
        Assert.Empty(fixture.Events);
    }

    [Fact]
    public void HeldRevertAppendsHeldEventAndIsNeverRetried()
    {
        var fixture = new Incident(new(ActivationFailedMainRevert.Outcome.Held, Reason: "merge conflict"));
        var guard = fixture.CreateGuard(Landing, Eligible("one", "two"));

        Assert.Equal(HandleStatus.Held, guard.ObserveTick(SharedHolds(), false, false, 2, AdoptedAt.AddMinutes(1)));
        Assert.Equal(HandleStatus.Suppressed, guard.ObserveTick(SharedHolds(), false, false, 2, AdoptedAt.AddMinutes(2)));
        Assert.Equal(HandleStatus.Suppressed, guard.ObserveTick(SharedHolds(), false, false, 2, AdoptedAt.AddMinutes(3)));

        Assert.Equal(Landing, Assert.Single(fixture.Reverts));
        var conductEvent = Assert.Single(fixture.Events);
        Assert.StartsWith($"POST_ADOPTION_REGRESSION_REVERT_HELD landing={Landing} ", conductEvent);
        Assert.Contains("reason=\"merge conflict\"", conductEvent);
    }

    [Theory]
    [InlineData(null, true, "no-landing-commit")]
    [InlineData(Landing, false, "no-adoption-baseline")]
    public void UnknownLandingCommitOrMissingBaselineRecordsInactiveAndNeverReverts(
        string? landing, bool hasBaseline, string reason)
    {
        var fixture = new Incident();
        var guard = fixture.CreateGuard(landing, hasBaseline ? Eligible("one", "two") : null);

        Assert.Null(guard.ObserveTick(SharedHolds(), false, true, 2, AdoptedAt.AddMinutes(1)));
        Assert.Null(guard.ObserveTick(SharedHolds(), false, true, 2, AdoptedAt.AddMinutes(2)));
        Assert.Equal($"POST_ADOPTION_GUARD_INACTIVE reason={reason} landing={landing ?? "unknown"}",
            Assert.Single(fixture.Diagnostics));
        Assert.Empty(fixture.Reverts);
        Assert.Empty(fixture.Events);
    }

    [Fact]
    public void ConductorTickPassesHoldDecisionsGateStateAndVerifiedCountToTheGuardSeam()
    {
        var (kernel, goal) = ConductorDriverTests.SimpleGoal();
        ConductorDriverTests.PassVerification(kernel, goal, goal.Tasks.Single());
        var otherVerified = GoalLifecycleCommands.CreateAndActivateSimpleGoal(
            kernel, ConductorDriverTests.DefaultAgents(), "Second verified goal");
        ConductorDriverTests.PassVerification(kernel, otherVerified, otherVerified.Tasks.Single());
        var gateGoal = kernel.CreateGoal("Goal occupying a gate slot");
        var root = Directory.CreateTempSubdirectory("mcg-regression-guard-");
        try
        {
            var coordinator = ConductorDriverTestHelpers.SeedLiveAcceptanceAttempt(root.FullName, gateGoal);
            Assert.Single(coordinator.GetAllCapacityReservingAttempts());
            var lifecycleReads = 0;
            var driver = new ConductorDriver(
                _ => ++lifecycleReads == 1 ? GoalLifecycleFacts.None : new(IsBlocked: true),
                () => 0, _ => root.FullName, _ => DispatchStartOutcome.Started(), null, null,
                _ => throw new InvalidOperationException("A blocked goal must not run inline acceptance."),
                null, null, null, (_, _, _) => 0, null,
                _ => throw new InvalidOperationException("A blocked goal must not rebase."),
                (_, _) => throw new InvalidOperationException("A blocked goal must not land."),
                null, _ => { }, _ => throw new InvalidOperationException("A blocked goal must not be cleaned up."),
                (_, _, _) => { }, _ => null,
                runAcceptanceVerificationWithSlot: (_, _) =>
                    throw new InvalidOperationException("A blocked goal must not run parallel acceptance."),
                getAcceptanceSlotCount: _ => 2, parallelAcceptanceAttemptCoordinator: coordinator,
                utcNow: () => AdoptedAt);
            var observer = new RecordingObserver();
            PostAdoptionRegressionGuard.Attach(driver, observer);
            driver.BeginTick(kernel, 7);

            var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

            var held = Assert.IsType<ConductorAdvanceOutcome.Held>(result.Outcome);
            var decision = Assert.IsType<PolicyDecisionRecord>(held.Decision);
            Assert.Equal("Hold", decision.Action);
            Assert.Equal("no-parallel-acceptance-candidate", decision.DiscriminatingEvidence);
            var seen = Assert.Single(observer.Results);
            Assert.Equal(7, seen.Tick);
            Assert.Equal(AdoptedAt, seen.At);
            Assert.Equal(goal.Id.Value, seen.GoalId);
            Assert.Same(decision, seen.Decision);
            Assert.True(seen.GateRunning);
            Assert.True(seen.GateSlotFree);
            Assert.Equal(2, seen.VerifiedGoalCount);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    private static GoalDecision[] Eligible(params string[] ids) =>
        ids.Select(id => new GoalDecision(id, EligibleBeforeAdoption: true, Decision: null)).ToArray();

    private static GoalDecision[] SharedHolds() =>
        [Held("one", "shared blocker"), Held("two", "shared blocker")];

    private static GoalDecision Held(string id, string reason) => new(id, false, Hold(reason));

    private static PolicyDecisionRecord Hold(string reason) =>
        new("solo-landing-admission", "Hold", 1, "no-parallel-acceptance-candidate", reason, []);

    private sealed class Incident(ActivationFailedMainRevert.Result? result = null)
    {
        internal List<string?> Reverts { get; } = [];
        internal List<string> Events { get; } = [];
        internal List<string> Diagnostics { get; } = [];

        internal PostAdoptionRegressionGuard CreateGuard(string? landing, IReadOnlyList<GoalDecision>? baseline) =>
            new(landing, baseline, AdoptedAt, new PostAdoptionRegressionReverter(null, null, Events.Add, (_, _, commit) =>
            {
                Reverts.Add(commit);
                return result ?? new(ActivationFailedMainRevert.Outcome.Reverted, RevertCommit: "revert-sha");
            }), Diagnostics.Add);
    }

    private sealed class RecordingObserver : IPostAdoptionRegressionTickObserver
    {
        internal sealed record Observation(int Tick, DateTimeOffset At, string GoalId, PolicyDecisionRecord? Decision,
            bool GateRunning, bool GateSlotFree, int VerifiedGoalCount);

        internal List<Observation> Results { get; } = [];

        public void ObserveGoalResult(int tick, DateTimeOffset now, string goalId, PolicyDecisionRecord? decision,
            bool gateRunning, bool gateSlotFree, int verifiedGoalCount) =>
            Results.Add(new(tick, now, goalId, decision, gateRunning, gateSlotFree, verifiedGoalCount));
    }
}
