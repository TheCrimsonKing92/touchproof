using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using static Mcg.AgentOrchestrator.App.Orchestration.PostAdoptionRegressionEvaluator;

// Parallel-safe: each test supplies its own decisions and adoption counters.
public sealed class PostAdoptionRegressionEvaluatorTests
{
    internal const string Blocker = "No background acceptance attempt could be scheduled (verification-incomplete)";

    [Fact]
    public void SharedBlockerAtBothWindowLimitsKeepsTheAffectedGoals()
    {
        var observation = SharedBlockerObservation() with
        {
            TicksSinceAdoption = 10,
            ElapsedSinceAdoption = TimeSpan.FromMinutes(30)
        };

        var verdict = new PostAdoptionRegressionEvaluator().Evaluate(observation);

        Assert.True(verdict.IsRegression);
        Assert.Equal("shared-blocker", verdict.Trigger);
        Assert.Equal(observation.AdoptedLandingCommit, verdict.AdoptedLandingCommit);
        Assert.Equal(Blocker, verdict.SharedReason);
        Assert.Equal(new[] { "first", "second" }, verdict.AffectedGoals.Select(goal => goal.GoalId));
    }

    [Fact]
    public void ACustomWindowExpiresAtEitherLimit()
    {
        var observation = SharedBlockerObservation() with
        {
            TicksSinceAdoption = 2,
            ElapsedSinceAdoption = TimeSpan.FromMinutes(1)
        };
        var evaluator = new PostAdoptionRegressionEvaluator(new Window(2, TimeSpan.FromMinutes(1)));

        Assert.True(evaluator.Evaluate(observation).IsRegression);
        Assert.False(evaluator.Evaluate(observation with { TicksSinceAdoption = 3 }).IsRegression);
        Assert.False(evaluator.Evaluate(observation with
        {
            ElapsedSinceAdoption = observation.ElapsedSinceAdoption.Add(TimeSpan.FromTicks(1))
        }).IsRegression);
    }

    [Theory]
    [InlineData("blocked", "Blocked")]
    [InlineData("blocked", "blocked ")]
    [InlineData("", "")]
    public void OnlyNonemptyOrdinallyEqualReasonsMatch(string firstReason, string secondReason)
    {
        var observation = SharedBlockerObservation() with
        {
            CurrentTick = [Held("first", firstReason), Held("second", secondReason)]
        };

        Assert.False(new PostAdoptionRegressionEvaluator().Evaluate(observation).IsRegression);
    }

    [Fact]
    public void OnlyDistinctPreviouslyEligibleGoalsWithCurrentHoldsCount()
    {
        var shared = SharedBlockerObservation();
        var observation = shared with
        {
            Baseline = [.. shared.Baseline, new("ineligible", false, null)],
            CurrentTick =
            [
                Held("first", "superseded"),
                Held("first", Blocker),
                new("second", false, Decision("hold", Blocker)),
                Held("ineligible", Blocker),
                Held("absent-from-baseline", Blocker)
            ]
        };

        Assert.False(new PostAdoptionRegressionEvaluator().Evaluate(observation).IsRegression);
    }

    [Theory]
    [InlineData(false, 1, false)]
    [InlineData(true, 0, false)]
    [InlineData(true, 1, true)]
    public void StarvationUsesTheGateSlotAndVerifiedCount(bool slotFree, int verifiedCount, bool expected)
    {
        var observation = SharedBlockerObservation() with
        {
            CurrentTick = [Held("first", Blocker)],
            GateSlotFree = slotFree,
            VerifiedGoalCount = verifiedCount
        };

        var verdict = new PostAdoptionRegressionEvaluator().Evaluate(observation);

        Assert.Equal(expected, verdict.IsRegression);
        Assert.Equal(expected, verdict.GateStarved);
        Assert.Empty(verdict.AffectedGoals);
    }

    [Fact]
    public void BothTriggersRetainOnlyTheSharedBlockerGoals()
    {
        var observation = SharedBlockerObservation() with { GateSlotFree = true, VerifiedGoalCount = 5 };

        var verdict = new PostAdoptionRegressionEvaluator().Evaluate(observation);

        Assert.True(verdict.IsRegression);
        Assert.True(verdict.GateStarved);
        Assert.Equal("shared-blocker+gate-starvation", verdict.Trigger);
        Assert.Equal(new[] { "first", "second" }, verdict.AffectedGoals.Select(goal => goal.GoalId));
    }

    internal static Observation SharedBlockerObservation(string landing = "adopted-merge") =>
        new(landing,
            [
                new("first", true, new("dispatch", "Dispatch", 2, "worker-available", "Worker ready",
                    [new("capacity", "slot\navailable")])),
                new("second", true, new("acceptance", "Admit", 3, "has-eligible-candidate", "Gate eligible",
                    [new("candidate", "ready")])),
                new("unaffected", true, null)
            ],
            [Held("first", Blocker), Held("second", Blocker), Held("unaffected", "Goal-specific issue")],
            1, TimeSpan.FromMinutes(1), GateRunning: false, GateSlotFree: false, VerifiedGoalCount: 0);

    internal static GoalDecision Held(string goalId, string reason) =>
        new(goalId, false, Decision("Hold", reason));

    private static PolicyDecisionRecord Decision(string action, string reason) =>
        new("solo-landing-admission", action, 1, "no-parallel-acceptance-candidate", reason,
            [new("candidateDeclineReason", "verification-incomplete")]);
}
