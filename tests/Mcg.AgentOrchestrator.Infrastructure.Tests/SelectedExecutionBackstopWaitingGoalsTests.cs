using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: kernel snapshots, clock, runner and all store identities belong to each test.
public sealed class SelectedExecutionBackstopWaitingGoalsTests
{
    private const string Candidate = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string PreviousCandidate = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    [Fact]
    public async Task VerifiedGoalOutsideEligibleList_YieldsAndRequestStaysQueuedWithZeroAttempts()
    {
        using var fixture = new SelectedExecutionBackstopFixture();
        await fixture.Land();
        var goal = VerifiedGoal();
        var waiting = SelectedExecutionBackstopWaitingGoals.Select([goal],
            _ => new GateReadyCandidateProjectionResult.Excluded(GateReadyCandidateExclusionReason.GateNotReady), _ => false);
        Assert.Same(goal, Assert.Single(waiting));

        await fixture.Coordinator.ServiceAsync(() => fixture.Head, [], waiting, true, false);
        await fixture.Coordinator.DrainAsync();

        Assert.Empty(fixture.Calls);
        var queued = Assert.IsType<SelectedExecutionBackstopRequest>((await fixture.Store.ReadAsync())!.Request);
        Assert.Equal(0, queued.Attempts);
        Assert.DoesNotContain(fixture.Lines, line => line.Contains("result=started "));
        // With the yield set cleared, the same request can start on this warmed coordinator.
        await fixture.Finish();
        Assert.Equal(queued.Id, Assert.Single(fixture.Calls).Id);
        AssertIdleStart(fixture);
    }

    [Fact]
    public async Task GoalHeldByOpenOwnerRequest_IsNotWaiting_RunStartsWithRuleIdle()
    {
        using var fixture = new SelectedExecutionBackstopFixture();
        await fixture.Land();
        var goal = VerifiedGoal();
        var waiting = SelectedExecutionBackstopWaitingGoals.Select([goal],
            _ => new GateReadyCandidateProjectionResult.Excluded(GateReadyCandidateExclusionReason.LandingHeld)
                { HoldRequestId = "open-owner-request" }, _ => false);
        Assert.Empty(waiting);

        await fixture.Coordinator.ServiceAsync(() => fixture.Head, [], waiting, true, false);
        await fixture.Coordinator.DrainAsync();

        Assert.Single(fixture.Calls);
        AssertIdleStart(fixture);
    }

    [Fact]
    public async Task PassedOutcomeForCurrentCandidate_IsNotWaiting_RunStarts()
    {
        using var fixture = new SelectedExecutionBackstopFixture();
        await fixture.Land();
        var goal = VerifiedGoal();
        var attempt = PassedAttempt(goal);
        Assert.True(SelectedExecutionBackstopWaitingGoals.HasPassedOutcome(goal, attempt, Candidate));
        var waiting = SelectedExecutionBackstopWaitingGoals.Select([goal], _ => null,
            item => SelectedExecutionBackstopWaitingGoals.HasPassedOutcome(item, attempt, Candidate));
        Assert.Empty(waiting);

        await fixture.Coordinator.ServiceAsync(() => fixture.Head, [], waiting, true, false);
        await fixture.Coordinator.DrainAsync();

        Assert.Single(fixture.Calls);
        AssertIdleStart(fixture);
    }

    [Fact]
    public async Task FirstServiceTickAfterConstruction_StartsNothing_SecondTickWithSameInputsStarts()
    {
        using var fixture = new SelectedExecutionBackstopFixture();
        await fixture.Land();
        var coordinator = fixture.CreateCoordinator(consumeInitialTick: false);

        await coordinator.ServiceAsync(() => fixture.Head, [], [], true, false);
        await coordinator.DrainAsync();

        Assert.Empty(fixture.Calls);
        var queued = Assert.IsType<SelectedExecutionBackstopRequest>((await fixture.Store.ReadAsync())!.Request);
        Assert.Equal(0, queued.Attempts);
        Assert.Contains(fixture.Lines, line => line.Contains("result=queued "));
        Assert.DoesNotContain(fixture.Lines, line => line.Contains("result=started "));

        await coordinator.ServiceAsync(() => fixture.Head, [], [], true, false);
        await coordinator.DrainAsync();

        var call = Assert.Single(fixture.Calls);
        Assert.Equal(queued.Id, call.Id);
        Assert.Equal(1, call.Attempts);
        AssertIdleStart(fixture);
    }

    [Theory]
    [InlineData((int)GateReadyCandidateExclusionReason.LandingHeld, null)]
    [InlineData((int)GateReadyCandidateExclusionReason.LandingHeld, "")]
    [InlineData((int)GateReadyCandidateExclusionReason.OwnerReviewHold, "other-request")]
    [InlineData((int)GateReadyCandidateExclusionReason.AdmissionFactsPending, null)]
    public async Task ExclusionWithoutOpenLandingHold_StillWaitsAndYields(int reasonValue, string? requestId)
    {
        using var fixture = new SelectedExecutionBackstopFixture();
        await fixture.Land();
        var goal = VerifiedGoal();
        var waiting = SelectedExecutionBackstopWaitingGoals.Select([goal],
            _ => new GateReadyCandidateProjectionResult.Excluded((GateReadyCandidateExclusionReason)reasonValue)
                { HoldRequestId = requestId }, _ => false);
        Assert.Same(goal, Assert.Single(waiting));

        await fixture.Coordinator.ServiceAsync(() => fixture.Head, [], waiting, true, false);
        await fixture.Coordinator.DrainAsync();

        Assert.Empty(fixture.Calls);
        Assert.Equal(0, (await fixture.Store.ReadAsync())!.Request!.Attempts);
    }

    [Theory]
    [InlineData("previous-candidate")]
    [InlineData("superseded")]
    [InlineData("regate")]
    [InlineData("another-goal")]
    [InlineData("focused-evidence")]
    [InlineData("failed")]
    [InlineData("missing")]
    [InlineData("unknown-candidate")]
    public async Task UnusablePassedOutcome_StillWaitsAndYields(string scenario)
    {
        using var fixture = new SelectedExecutionBackstopFixture();
        await fixture.Land();
        var goal = VerifiedGoal();
        ConductorParallelAcceptanceAttempt? attempt = PassedAttempt(goal);
        attempt = scenario switch
        {
            "previous-candidate" => attempt with { BranchHeadSha = PreviousCandidate },
            "superseded" => attempt with { SupersededBy = "new-attempt" },
            "regate" => attempt with { RegateRequestedAt = fixture.Now },
            "another-goal" => attempt with { GoalId = "another-goal" },
            "focused-evidence" => attempt with { Kind = "focused-evidence" },
            "failed" => attempt with { Outcome = ConductorParallelAcceptanceAttemptOutcome.Failed },
            "missing" => null,
            _ => attempt
        };
        var candidate = scenario == "unknown-candidate" ? null : Candidate;
        var waiting = SelectedExecutionBackstopWaitingGoals.Select([goal], _ => null,
            item => SelectedExecutionBackstopWaitingGoals.HasPassedOutcome(item, attempt, candidate));
        Assert.Same(goal, Assert.Single(waiting));

        await fixture.Coordinator.ServiceAsync(() => fixture.Head, [], waiting, true, false);
        await fixture.Coordinator.DrainAsync();

        Assert.Empty(fixture.Calls);
        Assert.Equal(0, (await fixture.Store.ReadAsync())!.Request!.Attempts);
    }

    [Fact]
    public void NonVerifiedGoal_DoesNotReadProjectionOrOutcome()
    {
        var goal = new AgentOrchestratorKernel().CreateGoal("not yet verified");
        Assert.NotEqual(GoalStatus.Verified, goal.Status);
        Assert.Empty(SelectedExecutionBackstopWaitingGoals.Select([goal],
            _ => throw new InvalidOperationException("Non-Verified goal was projected."),
            _ => throw new InvalidOperationException("Non-Verified outcome was read.")));
    }

    [Fact]
    public async Task EmptyStoreFirstTick_ConsumesSuppressionBeforeLanding()
    {
        using var fixture = new SelectedExecutionBackstopFixture();
        var coordinator = fixture.CreateCoordinator(consumeInitialTick: false);
        await coordinator.ServiceAsync(() => fixture.Head, [], [], false, true);
        Assert.Null(await fixture.Store.ReadAsync());
        await coordinator.NoteLandingAsync("selected-goal", "main-1", SelectedExecutionBackstopFixture.Applied, "main-0");

        await coordinator.ServiceAsync(() => fixture.Head, [], [], true, false);
        await coordinator.DrainAsync();

        Assert.Single(fixture.Calls);
        AssertIdleStart(fixture);
    }

    private static void AssertIdleStart(SelectedExecutionBackstopFixture fixture)
    {
        var line = Assert.Single(fixture.Lines.Where(line => line.Contains("result=started ")));
        Assert.Contains("rule=idle", line.Split(' '));
    }

    private static Goal VerifiedGoal()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("backstop waiting fixture");
        var snapshot = kernel.ExportSnapshot();
        kernel.ReplaceWithSnapshot(snapshot with
        {
            Goals = snapshot.Goals.Select(item => item with { Status = GoalStatus.Verified }).ToArray()
        });
        return kernel.GetGoal(goal.Id);
    }

    private static ConductorParallelAcceptanceAttempt PassedAttempt(Goal goal) => new(
        "passed-attempt", goal.Id.Value, goal.Id.Value[..8], 0, Candidate, PreviousCandidate,
        DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, 0,
        ConductorParallelAcceptanceAttemptOutcome.Passed, "", "", "", "", "passed.result.json", "passed.attempt.json");
}
