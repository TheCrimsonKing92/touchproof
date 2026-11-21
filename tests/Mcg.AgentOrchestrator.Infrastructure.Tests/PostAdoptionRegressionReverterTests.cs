using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using static PostAdoptionRegressionEvaluatorTests;
using Repository = ActivationFailedMainRevertTests.Repository;

// Parallel-safe: dependencies are instance-local and the real revert test uses a unique temporary repository.
public sealed class PostAdoptionRegressionReverterTests
{
    [Fact]
    public void SharedBlockerRevertsTheAdoptedLandingAndAppendsEveryDecisionPair()
    {
        var observation = SharedBlockerObservation();
        var calls = new List<(string? Root, string? Branch, string? Landing)>();
        var events = new List<string>();
        var reverter = new PostAdoptionRegressionReverter("integration-root", "main", events.Add,
            (root, branch, landing) =>
            {
                calls.Add((root, branch, landing));
                return new(ActivationFailedMainRevert.Outcome.Reverted, "revert-commit");
            });

        var verdict = new PostAdoptionRegressionEvaluator().Evaluate(observation);
        var result = reverter.Handle(verdict);

        Assert.True(verdict.IsRegression);
        Assert.Equal(("integration-root", "main", observation.AdoptedLandingCommit), Assert.Single(calls));
        Assert.Equal(PostAdoptionRegressionReverter.HandleStatus.Reverted, result.Status);
        var line = Assert.Single(events);
        Assert.StartsWith($"POST_ADOPTION_REGRESSION_REVERTED landing={observation.AdoptedLandingCommit} " +
            "revertCommit=revert-commit trigger=shared-blocker ", line);
        Assert.DoesNotContain("\n", line, StringComparison.Ordinal);
        var goals = ReadAffectedGoals(line);
        Assert.Equal(new[] { "first", "second" }, goals.Select(goal => goal.GoalId));
        foreach (var goal in goals)
        {
            var before = observation.Baseline.Single(decision => decision.GoalId == goal.GoalId);
            var after = observation.CurrentTick.Single(decision => decision.GoalId == goal.GoalId);
            AssertDecision(before.Decision!, Assert.IsType<PolicyDecisionRecord>(goal.Before));
            AssertDecision(after.Decision!, goal.After);
        }
    }

    [Fact]
    public void StarvationRevertsTheAdoptedLandingAndAppendsSchedulingEvidence()
    {
        var observation = SharedBlockerObservation() with
        {
            CurrentTick = [], GateSlotFree = true, VerifiedGoalCount = 4
        };
        var calls = new List<string?>();
        var events = new List<string>();
        var reverter = new PostAdoptionRegressionReverter("integration-root", "main", events.Add,
            (_, _, landing) =>
            {
                calls.Add(landing);
                return new(ActivationFailedMainRevert.Outcome.Reverted, "revert-commit");
            });

        var verdict = new PostAdoptionRegressionEvaluator().Evaluate(observation);
        var result = reverter.Handle(verdict);

        Assert.True(verdict.IsRegression);
        Assert.Equal(observation.AdoptedLandingCommit, Assert.Single(calls));
        Assert.Equal(PostAdoptionRegressionReverter.HandleStatus.Reverted, result.Status);
        var line = Assert.Single(events);
        Assert.Contains("trigger=gate-starvation ", line, StringComparison.Ordinal);
        Assert.Contains("gateStarved=True gateSlotFree=True verifiedGoals=4 ", line, StringComparison.Ordinal);
        Assert.Empty(ReadAffectedGoals(line));
    }

    [Theory]
    [InlineData("single-goal")]
    [InlineData("different-reasons")]
    [InlineData("previously-held-reason")]
    [InlineData("tick-limit")]
    [InlineData("time-limit")]
    [InlineData("running-gate")]
    public void NegativeEvidenceMakesNoRevertCallOrEvent(string scenario)
    {
        var shared = SharedBlockerObservation();
        var observation = scenario switch
        {
            "single-goal" => shared with { CurrentTick = [Held("first", "Goal-specific issue")] },
            "different-reasons" => shared with
            {
                CurrentTick = [Held("first", "First issue"), Held("second", "Second issue")]
            },
            "previously-held-reason" => shared with { Baseline = [.. shared.Baseline, Held("other", Blocker)] },
            "tick-limit" => shared with { TicksSinceAdoption = 11 },
            "time-limit" => shared with { ElapsedSinceAdoption = TimeSpan.FromMinutes(30).Add(TimeSpan.FromTicks(1)) },
            "running-gate" => shared with { GateRunning = true, GateSlotFree = true, VerifiedGoalCount = 4 },
            _ => throw new ArgumentOutOfRangeException(nameof(scenario))
        };
        var calls = 0;
        var events = new List<string>();
        var reverter = new PostAdoptionRegressionReverter("integration-root", "main", events.Add, (_, _, _) =>
        {
            calls++;
            return new(ActivationFailedMainRevert.Outcome.Reverted, "revert-commit");
        });

        var verdict = new PostAdoptionRegressionEvaluator().Evaluate(observation);
        var result = reverter.Handle(verdict);

        Assert.False(verdict.IsRegression);
        Assert.Equal(PostAdoptionRegressionReverter.HandleStatus.NotRegression, result.Status);
        Assert.Equal(0, calls);
        Assert.Empty(events);
    }

    [Fact]
    public void ClaimsTheIncidentBeforeRevertingAndSuppressesEvenADifferentLanding()
    {
        var evaluator = new PostAdoptionRegressionEvaluator();
        var first = evaluator.Evaluate(SharedBlockerObservation());
        var second = evaluator.Evaluate(SharedBlockerObservation("another-merge"));
        var calls = new List<string?>();
        var events = new List<string>();
        PostAdoptionRegressionReverter reverter = null!;
        reverter = new("integration-root", "main", events.Add, (_, _, landing) =>
        {
            calls.Add(landing);
            if (calls.Count == 1)
            {
                Assert.Equal(PostAdoptionRegressionReverter.HandleStatus.Suppressed, reverter.Handle(second).Status);
            }
            return new(ActivationFailedMainRevert.Outcome.Reverted, "revert-commit");
        });

        Assert.True(first.IsRegression);
        Assert.True(second.IsRegression);
        Assert.Equal(PostAdoptionRegressionReverter.HandleStatus.Reverted, reverter.Handle(first).Status);
        Assert.Equal(PostAdoptionRegressionReverter.HandleStatus.Suppressed, reverter.Handle(first).Status);
        Assert.Equal(PostAdoptionRegressionReverter.HandleStatus.Suppressed, reverter.Handle(second).Status);
        Assert.Equal(first.AdoptedLandingCommit, Assert.Single(calls));
        Assert.Single(events);
    }

    [Fact]
    public void ARealHeldRevertAppendsOneHoldAndLeavesTheIntegrationTipUntouched()
    {
        using var repository = new Repository();
        var initialTip = repository.Tip;
        var verdict = new PostAdoptionRegressionEvaluator().Evaluate(SharedBlockerObservation(repository.FirstParent));
        var events = new List<string>();
        var reverter = new PostAdoptionRegressionReverter(repository.Root, Repository.Branch, events.Add);

        var result = reverter.Handle(verdict);
        var duplicate = reverter.Handle(verdict with { AdoptedLandingCommit = repository.Merge });

        Assert.Equal(PostAdoptionRegressionReverter.HandleStatus.Held, result.Status);
        Assert.Equal(PostAdoptionRegressionReverter.HandleStatus.Suppressed, duplicate.Status);
        Assert.Equal(initialTip, repository.Tip);
        var line = Assert.Single(events);
        Assert.StartsWith($"POST_ADOPTION_REGRESSION_REVERT_HELD landing={repository.FirstParent} " +
            "reason=\"failed commit is not a two-parent merge\" ", line);
        Assert.Equal(2, ReadAffectedGoals(line).Length);
    }

    [Fact]
    public void NotApplicableAppendsItsOutcomeWithoutAReasonAndConsumesTheIncident()
    {
        var verdict = new PostAdoptionRegressionEvaluator().Evaluate(SharedBlockerObservation());
        var calls = 0;
        var events = new List<string>();
        var reverter = new PostAdoptionRegressionReverter("integration-root", "main", events.Add, (_, _, _) =>
        {
            calls++;
            return new(ActivationFailedMainRevert.Outcome.NotApplicable);
        });

        var result = reverter.Handle(verdict);
        var duplicate = reverter.Handle(verdict);

        Assert.Equal(PostAdoptionRegressionReverter.HandleStatus.NotApplicable, result.Status);
        Assert.Null(result.Event?.Reason);
        Assert.Equal(PostAdoptionRegressionReverter.HandleStatus.Suppressed, duplicate.Status);
        Assert.Equal(1, calls);
        var line = Assert.Single(events);
        Assert.StartsWith($"POST_ADOPTION_REGRESSION_REVERT_NOT_APPLICABLE landing={verdict.AdoptedLandingCommit} " +
            "trigger=shared-blocker ", line);
        Assert.Equal(2, ReadAffectedGoals(line).Length);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AThrowingOrCancelledRevertSurfacesTheFailureAndConsumesTheIncident(bool cancelled)
    {
        var verdict = new PostAdoptionRegressionEvaluator().Evaluate(SharedBlockerObservation());
        Exception failure = cancelled ? new OperationCanceledException() : new InvalidOperationException();
        var calls = 0;
        var events = new List<string>();
        var reverter = new PostAdoptionRegressionReverter("integration-root", "main", events.Add, (_, _, _) =>
        {
            calls++;
            throw failure;
        });

        Assert.Same(failure, Record.Exception(() => { reverter.Handle(verdict); }));
        Assert.Equal(PostAdoptionRegressionReverter.HandleStatus.Suppressed, reverter.Handle(verdict).Status);
        Assert.Equal(1, calls);
        Assert.Empty(events);
    }

    private static PostAdoptionRegressionEvaluator.AffectedGoal[] ReadAffectedGoals(string line)
    {
        const string prefix = " goals=";
        var start = line.IndexOf(prefix, StringComparison.Ordinal);
        Assert.True(start >= 0, "Expected the appended event to include goals.");
        return Assert.IsType<PostAdoptionRegressionEvaluator.AffectedGoal[]>(
            JsonSerializer.Deserialize<PostAdoptionRegressionEvaluator.AffectedGoal[]>(line[(start + prefix.Length)..]));
    }

    private static void AssertDecision(PolicyDecisionRecord expected, PolicyDecisionRecord actual)
    {
        Assert.Equal(expected.Facts.ToArray(), actual.Facts.ToArray());
        Assert.Equal(expected with { Facts = actual.Facts }, actual);
    }
}
