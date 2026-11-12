using Mcg.AgentOrchestrator.App.Application;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using static ConductorDriverTests;
using static AttributedFailureDeveloperRouteTests;

public sealed class SoloAttributedFailureRouteReplayTests
{
    [Fact(Timeout = TestHangGuard.WholeTestMilliseconds)]
    [Trait("Category", "CrossTick")]
    public async Task SameCandidateSignatureSurvivesRestartAndDifferentReceiptDigest()
    {
        using var fixture = new SoloAttributedFailureRouteFixture();
        await fixture.Produce();
        fixture.Complete();
        var driver = fixture.Driver();
        driver.BeginTick(fixture.Kernel, 2);
        driver.AdvanceOnce(fixture.Goal, ConductorAutonomyPolicy.Permissive with { AutomaticRetryOperatorRoutingHoldSeconds = 0 });
        Assert.Single(fixture.Starts);
        var task = fixture.Goal.Tasks.Single();
        AttributedFailureAutoRouteScenarioTests.CompleteRecordedProcess(fixture.Kernel, fixture.Goal, task);
        fixture.Kernel.RecordTaskVerification(fixture.Goal.Id, task.Id,
            new("fixture", fixture.Repo, 0, "No new commit", "", DateTimeOffset.UtcNow, HasCommittedChanges: false));
        Assert.Equal(GoalStatus.Verified, fixture.Goal.Status);
        var firstDigest = fixture.Summary.SoloFailureReceipt!.Evidence.Sha256;
        fixture.ReplaceProof(proof => proof with { Bindings = proof.Bindings.Reverse().ToArray() });
        Assert.NotEqual(firstDigest, fixture.Summary.SoloFailureReceipt!.Evidence.Sha256);
        fixture.Record(fixture.Summary);
        await fixture.Restart();
        driver = fixture.Driver();
        driver.BeginTick(fixture.Kernel, 3);
        var held = Assert.IsType<ConductorAdvanceOutcome.Held>(driver.AdvanceOnce(fixture.Goal, ConductorAutonomyPolicy.Permissive).Outcome);
        Assert.Contains("already-routed", held.Reason);
        Assert.Single(fixture.Starts);
        Assert.Single(MarkerRetries(fixture.Goal));
        Assert.Equal(1, fixture.Goal.AutomaticAcceptanceRetryCount);
    }

    [Fact(Timeout = TestHangGuard.WholeTestMilliseconds)]
    public async Task EarlierNonSoloRetriesDoNotCapUnseenSoloSignature()
    {
        using var fixture = new SoloAttributedFailureRouteFixture();
        await fixture.Produce();
        var task = fixture.Goal.Tasks.Single();
        for (var i = 0; i < ConductorAutonomyPolicy.Permissive.MaxCriterionRetries; i++)
            fixture.Kernel.RecordCriterionRetryFeedback(fixture.Goal.Id, task.Id, ["Earlier independent failure"]);
        fixture.Record(fixture.Summary);
        await fixture.Restart();
        var driver = fixture.Driver();
        driver.BeginTick(fixture.Kernel, 2);
        Assert.IsNotType<ConductorAdvanceOutcome.Held>(driver.AdvanceOnce(fixture.Goal,
            ConductorAutonomyPolicy.Permissive with { AutomaticRetryOperatorRoutingHoldSeconds = 0 }).Outcome);
        Assert.Single(MarkerRetries(fixture.Goal));
        Assert.Single(fixture.Starts);
        Assert.Equal(ConductorAutonomyPolicy.Permissive.MaxCriterionRetries + 1, fixture.Goal.AutomaticAcceptanceRetryCount);
    }

    [Theory(Timeout = TestHangGuard.WholeTestMilliseconds)]
    [Trait("Category", "CrossTick")]
    [InlineData(PaidRouteClassification.Paid)]
    [InlineData(PaidRouteClassification.NonPaid)]
    public async Task ClaimedStartWithoutConfirmationHoldsAcrossConductorRestart(PaidRouteClassification lane)
    {
        using var fixture = new SoloAttributedFailureRouteFixture();
        await fixture.Produce();
        fixture.Complete();
        fixture.BeforeStart = () => throw new InvalidOperationException("Interrupted before process start");
        var driver = fixture.Driver();
        driver.BeginTick(fixture.Kernel, 2);
        var interruption = Assert.Throws<InvalidOperationException>(() => driver.AdvanceOnce(fixture.Goal, ConductorAutonomyPolicy.Permissive with { AutomaticRetryOperatorRoutingHoldSeconds = 0 }));
        Assert.Equal("Interrupted before process start", interruption.Message);
        Assert.Single(MarkerRetries(fixture.Goal));
        Assert.Empty(fixture.Starts);

        var task = fixture.Goal.Tasks.Single();
        DispatchTask(fixture.Kernel, fixture.Goal, task);
        var fingerprint = RetryContextFingerprintBuilder.Build(new(fixture.Goal.Id.Value, task.Id.Value,
            task.RequiredRole, "OpenAI", "gpt", lane, fixture.Context.CandidateSha, "criteria", [], [], [], [], "base", "main"));
        var at = DateTimeOffset.UtcNow;
        var dispatch = task.LastDispatch! with { PaidRoute = lane, RetryContextFingerprint = fingerprint };
        var reserved = RetryAdmissionSnapshotReservation.Apply(fixture.Kernel.ExportGoalSnapshot(fixture.Goal.Id),
            task.Id, fingerprint, lane, RetryCause.NewTestFinding, dispatch, at, "solo-owner", at.AddMinutes(1));
        Assert.True(reserved.Admission.AllowsProcessStart);
        var claimed = RetryAdmissionSnapshotStartClaim.Apply(reserved.Snapshot, task.Id, dispatch.DispatchedAt, "solo-owner", at);
        Assert.True(claimed.Claimed);
        fixture.Kernel.ReplaceGoalWithSnapshot(claimed.Snapshot);
        task = fixture.Kernel.GetGoal(fixture.Goal.Id).Tasks.Single();
        Assert.True(GoalDispatchOperations.RequiresDurableAdmissionSnapshotReplacement(task));
        Assert.True(GoalDispatchOperations.HasUncertainSoloCorrectionStart(task));
        await fixture.Restart();
        fixture.BeforeStart = null;
        driver = fixture.Driver();
        driver.BeginTick(fixture.Kernel, 3);
        var held = Assert.IsType<ConductorAdvanceOutcome.Held>(driver.AdvanceOnce(fixture.Goal, ConductorAutonomyPolicy.Permissive).Outcome);
        Assert.Contains("solo-start-claimed-reconciliation-required", held.Reason);
        Assert.Empty(fixture.Starts);
        Assert.Single(MarkerRetries(fixture.Goal));
        Assert.Equal(1, fixture.Goal.AutomaticAcceptanceRetryCount);
    }

    [Fact(Timeout = TestHangGuard.WholeTestMilliseconds)]
    [Trait("Category", "CrossTick")]
    public async Task UnstartedCorrectionResumesWithoutAnotherRetryCharge()
    {
        using var fixture = new SoloAttributedFailureRouteFixture();
        await fixture.Produce();
        fixture.Complete();
        fixture.BeforeStart = () => throw new InvalidOperationException("Interrupted before dispatch preparation");
        var driver = fixture.Driver();
        driver.BeginTick(fixture.Kernel, 2);
        Assert.Throws<InvalidOperationException>(() => driver.AdvanceOnce(fixture.Goal, ConductorAutonomyPolicy.Permissive with { AutomaticRetryOperatorRoutingHoldSeconds = 0 }));
        Assert.Empty(fixture.Starts);
        Assert.Single(MarkerRetries(fixture.Goal));
        await fixture.Restart();
        fixture.BeforeStart = null;
        driver = fixture.Driver();
        driver.BeginTick(fixture.Kernel, 3);
        driver.AdvanceOnce(fixture.Goal, ConductorAutonomyPolicy.Permissive);
        Assert.Single(fixture.Starts);
        Assert.Single(MarkerRetries(fixture.Goal));
        Assert.Equal(1, fixture.Goal.AutomaticAcceptanceRetryCount);
    }
}
