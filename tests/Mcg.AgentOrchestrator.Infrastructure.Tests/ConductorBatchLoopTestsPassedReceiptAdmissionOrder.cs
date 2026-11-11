using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

// The live census probe is scoped to this collection; no real gate process is launched.
[Xunit.Collection(TestCollections.DotnetBuildSlots)]
public sealed class ConductorBatchLoopTestsPassedReceiptAdmissionOrder : ConductorBatchLoopTests
{
    public ConductorBatchLoopTestsPassedReceiptAdmissionOrder(ITestOutputHelper output) : base(output) { }

    [Xunit.Fact]
    public void ValidPassedReceiptAtFullWidthLandsWithoutCapacityHold()
    {
        using var scene = new Scene();
        var tick = scene.Advance();

        scene.AssertNoCapacityHold(tick);
        Assert.Equal(1, scene.Landings);
        Assert.NotNull(scene.Receipt.ReplayLandedAt);
        Assert.Contains(tick.ProgressLines!, line => line.Contains("result=replayed", StringComparison.Ordinal));
    }

    [Xunit.Theory(Timeout = TestHangGuard.WholeTestMilliseconds)]
    [Xunit.Trait("Category", "CrossTick")]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public void ActiveLandingHoldAtFullWidthPreservesReceiptWithoutCapacityHold(bool apparatus)
    {
        using var scene = new Scene();
        scene.HoldLanding(apparatus);
        var receipt = scene.Receipt;
        Assert.Equal(receipt.ReplayContextFingerprint,
            ConductorParallelAcceptanceAttemptCoordinator.BuildReplayContextFingerprint(scene.Goal));
        // This is the rejecting guard on the original code: candidate-none discards
        // hold-active, replay returns null, and solo admission reports acceptance width.
        Assert.Null(scene.Driver.TryBuildParallelAcceptanceCandidate(scene.Goal,
            ConductorAutonomyPolicy.Conservative, 0, out var decline));
        Assert.Equal(ConductorDriver.CandidateDeclineHoldActive, decline);

        var tick = scene.Advance();

        scene.AssertNoCapacityHold(tick);
        Assert.Contains(tick.ProgressLines!, line =>
            scene.IsGoalHold(line) && line.Contains(
                apparatus ? "apparatus" : "Owner_approval_required", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(0, scene.Landings);
        Assert.Equal(JsonSerializer.Serialize(receipt), JsonSerializer.Serialize(scene.Receipt));
        scene.Kernel.ClearAcceptanceFailure(scene.Goal.Id);
        scene.Advance();
        Assert.Equal(1, scene.Landings);
    }

    [Xunit.Fact]
    public void OlderCandidateReceiptAtFullWidthReceivesAcceptanceWidthHold()
    {
        using var scene = new Scene();
        scene.CurrentCandidate = new string('c', 40);
        scene.AssertCapacityHold(scene.Advance());
    }

    [Xunit.Fact]
    public void MainAdvancedWithOverlapAtFullWidthReceivesAcceptanceWidthHold()
    {
        using var scene = new Scene(native: true);
        scene.AdvanceMain("Directory.Build.props");
        scene.AssertCapacityHold(scene.Advance());
        Assert.Contains(GoalOperationJournal.Read(scene.Root, scene.Goal.Id).Entries, entry =>
            entry.Operation == "conductor:acceptance-main-advance" &&
            entry.Detail?.Contains("disposition=overlap", StringComparison.OrdinalIgnoreCase) == true);
    }

    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public void MainAdvancedDisjointAtFullWidthLandsWithoutCapacityHold(bool postLanding)
    {
        using var scene = new Scene(native: true, operatorObligation: postLanding, postLanding: postLanding);
        scene.AdvanceMain("src/unrelated.cs");
        scene.AssertNoCapacityHold(scene.Advance());
        Assert.Equal(1, scene.Landings);
        Assert.NotNull(scene.Receipt.ReplayLandedAt);
    }

    [Xunit.Fact]
    public void NoPassedAttemptAtFullWidthReceivesAcceptanceWidthHold()
    {
        using var scene = new Scene(seedReceipt: false);
        scene.AssertCapacityHold(scene.Advance());
    }

    [Xunit.Fact]
    public void PendingGateEvidenceWithLandingHoldStillRequiresCapacity()
    {
        using var scene = new Scene();
        scene.Kernel.MapCriterionEvidenceOwner(scene.Goal.Id, 0, 1, CriterionEvidenceOwner.Acceptance,
            "fixture prior binding", CriterionEvidenceScopes.FullAcceptanceGate, expectedCandidateSha: new string('d', 40));
        scene.Kernel.MapCriterionEvidenceOwner(scene.Goal.Id, 0, 1, CriterionEvidenceOwner.Acceptance,
            "fixture rebind", CriterionEvidenceScopes.FullAcceptanceGate, expectedCandidateSha: scene.Candidate);
        scene.HoldLanding(apparatus: false);
        Assert.NotEmpty(scene.Goal.GetOutstandingCriterionEvidenceObligations(scene.Candidate));

        scene.AssertCapacityHold(scene.Advance());

        Assert.NotEmpty(scene.Goal.GetOutstandingCriterionEvidenceObligations(scene.Candidate));
    }

    [Xunit.Theory]
    [Xunit.InlineData("criteria")]
    [Xunit.InlineData("candidate")]
    [Xunit.InlineData("artifact")]
    [Xunit.InlineData("task")]
    [Xunit.InlineData("scope")]
    public void InvalidReceiptWithLandingHoldStillRequiresCapacity(string change)
    {
        using var scene = new Scene();
        switch (change)
        {
            case "criteria":
                scene.Kernel.RecordGoalRefinement(scene.Goal.Id, scene.Goal.RefinedSpec! with
                    { AcceptanceCriteria = ["Changed required behavior"] });
                break;
            case "candidate": scene.CurrentCandidate = new string('c', 40); break;
            case "artifact": File.AppendAllText(scene.Receipt.ResultPath, " "); break;
            case "task":
                scene.Kernel.RecordTaskVerification(scene.Goal.Id, scene.Goal.Tasks.Single().Id,
                    ManualVerificationRecorder.Create(true, "new verification", "fixture", DateTimeOffset.UtcNow));
                break;
            case "scope": scene.Scopes = ["src/changed.cs"]; break;
        }
        scene.HoldLanding(apparatus: false);
        scene.AssertCapacityHold(scene.Advance());
    }

    [Xunit.Theory(Timeout = TestHangGuard.WholeTestMilliseconds)]
    [Xunit.Trait("Category", "CrossTick")]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public void OperatorHoldSurvivesReconstructionAndMainDrift(bool fullWidth)
    {
        var measuredCandidate = new string('d', 40);
        using var scene = new Scene(native: true, operatorObligation: true, occupyCapacity: fullWidth,
            operatorCandidate: measuredCandidate);
        scene.RecordOperatorEvidence(passed: false, candidate: measuredCandidate);
        var failed = scene.Goal.CriterionEvidenceObligations.Single();
        Assert.Equal(measuredCandidate, failed.ExpectedCandidateSha);
        Assert.Equal(measuredCandidate, failed.CandidateSha);
        Assert.NotEqual(scene.Candidate, failed.CandidateSha);
        var receipt = JsonSerializer.Serialize(scene.Receipt);
        scene.Advance();
        var first = scene.Goal.LandingBlocker!;
        Assert.Equal("criterion-evidence-admission/operator-prelanding-repeat-gate", first.Kind);
        Assert.Contains(failed.Id, first.Detail);
        Assert.Equal("Operator", first.NextActor);
        Assert.Equal("criterion-evidence-record", first.NextVerb);
        Assert.Equal(PassedGateHoldCategory.CriterionEvidenceObligation,
            PassedGateHoldClassifier.Classify(scene.Goal, scene.Receipt.AttemptId, scene.Candidate)!.Category);

        scene.AdvanceMain("Directory.Build.props");
        scene.Reconstruct();
        var tick = scene.Advance();

        Assert.Contains(tick.ProgressLines!, line => line.Contains("reason=operator-prelanding-repeat-gate", StringComparison.Ordinal));
        Assert.Equal(first.Identity, scene.Goal.LandingBlocker!.Identity);
        var retained = scene.Goal.CriterionEvidenceObligations.Single();
        Assert.Equal(CriterionEvidenceState.Failed, retained.State);
        Assert.Equal(failed.CandidateSha, retained.CandidateSha);
        Assert.Equal(failed.ExpectedCandidateSha, retained.ExpectedCandidateSha);
        Assert.Equal(failed.ReceiptId, retained.ReceiptId);
        Assert.Equal(receipt, JsonSerializer.Serialize(scene.Receipt));
        Assert.Empty(scene.Coordinator.GetUnreconciledAttempts([scene.Goal.Id.Value]));
        var deferral = Assert.Single(GoalOperationJournal.Read(scene.Root, scene.Goal.Id).Entries.Where(entry =>
            entry.Operation == GoalOperationJournal.RepeatGateDeferralOperation));
        Assert.True(deferral.HasCandidate(scene.Candidate, scene.Main));
        var provenance = JsonSerializer.Deserialize<RepeatGateDeferralReceipt>(deferral.Detail!,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        Assert.Equal(scene.Receipt.AttemptId, provenance.AttemptId);
        Assert.Contains(failed.Id, provenance.ObligationIds);
        Assert.Equal(0, scene.Landings);
    }

    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public void AutomaticSoloPathsHoldBeforeNewGate(bool fallback)
    {
        using var scene = new Scene(operatorObligation: true, occupyCapacity: false);
        var receipt = JsonSerializer.Serialize(scene.Receipt);
        if (fallback)
        {
            scene.Driver.BeginTick(scene.Kernel, 1);
            var held = Assert.IsType<ConductorAdvanceOutcome.Held>(
                scene.Driver.AdvanceOnce(scene.Goal, ConductorAutonomyPolicy.Conservative).Outcome);
            Assert.Equal("operator-prelanding-repeat-gate", held.Decision!.DiscriminatingEvidence);
        }
        else
        {
            scene.Advance(width: 2);
            Assert.Equal("criterion-evidence-admission/operator-prelanding-repeat-gate", scene.Goal.LandingBlocker!.Kind);
        }
        Assert.Equal(0, scene.Gates);
        Assert.Equal(receipt, JsonSerializer.Serialize(scene.Receipt));
        Assert.Empty(scene.Coordinator.GetCapacityReservingAttempts([scene.Goal.Id.Value]));
    }

    [Xunit.Fact]
    public void OperatorObligationDoesNotSuppressFirstNecessaryGate()
    {
        using var scene = new Scene(seedReceipt: false, operatorObligation: true, occupyCapacity: false);
        scene.Advance(expectedGates: 1);
        Assert.Equal(ConductorParallelAcceptanceAttemptOutcome.Passed, scene.Receipt.Outcome);
        Assert.NotEqual("criterion-evidence-admission/operator-prelanding-repeat-gate", scene.Goal.LandingBlocker!.Kind);
    }

    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public void BusyMutationLeaseReportsLeaseHoldWithoutRecordingRepeatDeferral(bool fallback)
    {
        using var scene = new Scene(operatorObligation: true, occupyCapacity: false, blockMutationLease: true);
        var receipt = JsonSerializer.Serialize(scene.Receipt);
        if (fallback)
        {
            scene.Driver.BeginTick(scene.Kernel, 1);
            var held = Assert.IsType<ConductorAdvanceOutcome.Held>(
                scene.Driver.AdvanceOnce(scene.Goal, ConductorAutonomyPolicy.Conservative).Outcome);
            Assert.Equal("replacement-evidence-mutation-lease", held.Decision!.DiscriminatingEvidence);
        }
        else
        {
            var tick = scene.Advance(width: 2);
            Assert.Contains(tick.ProgressLines!, line => line.Contains(
                "reason=replacement-evidence-mutation-lease", StringComparison.Ordinal));
            Assert.DoesNotContain(tick.ProgressLines!, line => line.Contains(
                "reason=operator-prelanding-repeat-gate", StringComparison.Ordinal));
            Assert.Equal("verifying/replacement-evidence-mutation-lease", scene.Goal.LandingBlocker!.Kind);
            Assert.Equal("conductor", scene.Goal.LandingBlocker.NextActor);
        }
        Assert.DoesNotContain(GoalOperationJournal.Read(scene.Root, scene.Goal.Id).Entries,
            entry => entry.Operation == GoalOperationJournal.RepeatGateDeferralOperation);
        Assert.Equal(receipt, JsonSerializer.Serialize(scene.Receipt));
        Assert.Equal(0, scene.Gates);
        Assert.Empty(scene.Coordinator.GetCapacityReservingAttempts([scene.Goal.Id.Value]));
    }

    [Xunit.Theory]
    [Xunit.InlineData("missing")]
    [Xunit.InlineData("candidate")]
    [Xunit.InlineData("brief")]
    [Xunit.InlineData("artifact")]
    [Xunit.InlineData("focused")]
    [Xunit.InlineData("failed")]
    [Xunit.InlineData("fingerprint")]
    public void UnprovenHistoricalPassPreservesOrdinaryAdmission(string change)
    {
        using var scene = new Scene(seedReceipt: change != "missing", operatorObligation: true);
        switch (change)
        {
            case "candidate": scene.CurrentCandidate = new string('c', 40); break;
            case "brief":
                scene.Kernel.RecordGoalRefinement(scene.Goal.Id, scene.Goal.RefinedSpec! with
                    { BehavioralContract = "Changed authoritative contract" });
                break;
            case "artifact": File.AppendAllText(scene.Receipt.ResultPath, " "); break;
            case "focused": scene.RewriteReceipt(scene.Receipt with { Kind = "pre-review-evidence" }); break;
            case "failed": scene.RewriteReceipt(scene.Receipt with { Outcome = ConductorParallelAcceptanceAttemptOutcome.Failed }); break;
            case "fingerprint": scene.RewriteReceipt(scene.Receipt with { ReplayContextFingerprint = null }); break;
        }
        scene.AssertCapacityHold(scene.Advance());
        Assert.NotEqual("criterion-evidence-admission/operator-prelanding-repeat-gate", scene.Goal.LandingBlocker!.Kind);
    }

    [Xunit.Theory(Timeout = TestHangGuard.WholeTestMilliseconds)]
    [Xunit.Trait("Category", "CrossTick")]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public void MissingTrxPreservesOrdinaryReceiptReplay(bool operatorObligation)
    {
        using var scene = new Scene(operatorObligation: operatorObligation);
        var historical = scene.Receipt;
        File.Delete(historical.TestResultPaths!.Single());
        if (operatorObligation)
            scene.RecordOperatorEvidence(passed: false);
        var obligation = scene.Goal.CriterionEvidenceObligations.Single();

        // Missing execution evidence cannot establish the new scheduling hold, but
        // ordinary receipt replay retains its existing content-bound proof contract.
        Assert.Null(scene.Driver.TryHoldRepeatSoloAcceptance(scene.Goal, ConductorAutonomyPolicy.Conservative));
        var reuse = scene.Coordinator.Evaluate(
            ConductorParallelAcceptanceCandidate.Create(scene.Goal, 0, historical.ScopePaths!, scene.Candidate, scene.Main),
            ConductorAutonomyPolicy.Conservative,
            (_, _, _, _, _) => throw new InvalidOperationException("Ordinary receipt reuse must not launch a gate."));
        Assert.Equal(ConductorParallelAcceptanceAttemptDecisionKind.Completed, reuse.Kind);
        Assert.Equal(historical.AttemptId, reuse.Attempt.AttemptId);
        var tick = scene.Advance();

        scene.AssertNoCapacityHold(tick);
        Assert.Equal(historical.AttemptId, scene.Receipt.AttemptId);
        Assert.Equal(operatorObligation ? 0 : 1, scene.Landings);
        if (operatorObligation)
        {
            Assert.Null(scene.Receipt.ReplayLandedAt);
            Assert.NotEqual("criterion-evidence-admission/operator-prelanding-repeat-gate", scene.Goal.LandingBlocker!.Kind);
            var retained = scene.Goal.CriterionEvidenceObligations.Single();
            Assert.Equal(CriterionEvidenceState.Failed, retained.State);
            Assert.Equal(obligation.ReceiptId, retained.ReceiptId);
            scene.RecordOperatorEvidence(passed: true);
            scene.AssertNoCapacityHold(scene.Advance());
            Assert.Equal(1, scene.Landings);
            Assert.Equal(historical.AttemptId, scene.Receipt.AttemptId);
            Assert.NotNull(scene.Receipt.ReplayLandedAt);
        }
        else
            Assert.NotNull(scene.Receipt.ReplayLandedAt);
    }

    [Xunit.Theory(Timeout = TestHangGuard.WholeTestMilliseconds)]
    [Xunit.Trait("Category", "CrossTick")]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public void CandidateOrBriefChangeReleasesRepeatHold(bool changeBrief)
    {
        using var scene = new Scene(operatorObligation: true);
        scene.Advance();
        Assert.Equal("criterion-evidence-admission/operator-prelanding-repeat-gate", scene.Goal.LandingBlocker!.Kind);
        if (changeBrief)
            scene.Kernel.RecordGoalRefinement(scene.Goal.Id, scene.Goal.RefinedSpec! with
                { BehavioralContract = "Changed authoritative contract" });
        else
            scene.CurrentCandidate = new string('c', 40);
        scene.AssertCapacityHold(scene.Advance());
    }

    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public void AcceptanceAndPostLandingObligationsPreserveOrdinaryAdmission(bool postLanding)
    {
        using var scene = new Scene(operatorObligation: postLanding, postLanding: postLanding);
        Assert.Null(scene.Driver.TryHoldRepeatSoloAcceptance(scene.Goal, ConductorAutonomyPolicy.Conservative));
        scene.CurrentCandidate = new string('c', 40);
        scene.AssertCapacityHold(scene.Advance());
    }

    [Xunit.Theory(Timeout = TestHangGuard.WholeTestMilliseconds)]
    [Xunit.Trait("Category", "CrossTick")]
    [Xunit.InlineData("Directory.Build.props", false)]
    [Xunit.InlineData("Directory.Build.props", true)]
    [Xunit.InlineData("src/unrelated.cs", false)]
    [Xunit.InlineData("src/unrelated.cs", true)]
    public void ResolvingOperatorBlockerRequiresGateForAdvancedMain(string mainPath, bool reconstruct)
    {
        using var scene = new Scene(native: true, operatorObligation: true, occupyCapacity: false);
        scene.RecordOperatorEvidence(passed: false);
        scene.AdvanceMain(mainPath);
        scene.Advance();
        Assert.Equal("criterion-evidence-admission/operator-prelanding-repeat-gate", scene.Goal.LandingBlocker!.Kind);
        var historical = scene.Receipt;
        Assert.True(GoalOperationJournal.HasRepeatGateDeferral(scene.Root, scene.Goal, historical));
        scene.RecordOperatorEvidence(passed: false);
        scene.Advance();
        Assert.Equal(historical.AttemptId, scene.Receipt.AttemptId);

        scene.RecordOperatorEvidence(passed: true);
        // The visible blocker may be cleared independently. Release must retain its
        // scheduling provenance across reconstruction through the canonical journal.
        Assert.True(scene.Goal.ClearLandingBlocker());
        if (reconstruct) scene.Reconstruct();
        Assert.True(GoalOperationJournal.HasRepeatGateDeferral(scene.Root, scene.Goal, historical));
        Assert.Null(scene.Driver.TryHoldRepeatSoloAcceptance(scene.Goal, ConductorAutonomyPolicy.Conservative));
        scene.Advance(expectedGates: 1);

        Assert.NotEqual(historical.AttemptId, scene.Receipt.AttemptId);
        Assert.False(GoalOperationJournal.HasRepeatGateDeferral(scene.Root, scene.Goal, scene.Receipt));
        Assert.Equal(scene.CurrentMain, scene.Receipt.MainHeadSha);
        Assert.NotEqual(historical.MainHeadSha, scene.Receipt.MainHeadSha);
        Assert.Null(historical.ReplayLandedAt);
        Assert.Contains(scene.Goal.CriterionEvidenceObligations.Single().PriorReceipts!, receipt => !receipt.Passed);
    }

    [Xunit.Fact(Timeout = TestHangGuard.WholeTestMilliseconds)]
    [Xunit.Trait("Category", "CrossTick")]
    public void ResolvingOperatorBlockerWithoutMainDriftReplaysExactReceipt()
    {
        using var scene = new Scene(operatorObligation: true, occupyCapacity: false);
        scene.RecordOperatorEvidence(passed: false);
        scene.Advance();
        var historical = scene.Receipt;

        scene.RecordOperatorEvidence(passed: true);
        scene.Reconstruct();
        scene.Advance();

        Assert.Equal(1, scene.Landings);
        Assert.Equal(historical.AttemptId, scene.Receipt.AttemptId);
        Assert.NotNull(scene.Receipt.ReplayLandedAt);
    }

    [Xunit.Theory(Timeout = TestHangGuard.WholeTestMilliseconds)]
    [Xunit.Trait("Category", "CrossTick")]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public void RephasingOperatorBlockerRequiresGateForDisjointMain(bool reconstruct)
    {
        using var scene = new Scene(native: true, operatorObligation: true, occupyCapacity: false);
        scene.AdvanceMain("src/unrelated.cs");
        scene.Advance();
        Assert.Equal("criterion-evidence-admission/operator-prelanding-repeat-gate", scene.Goal.LandingBlocker!.Kind);
        var historical = scene.Receipt;
        var obligation = Assert.Single(scene.Goal.CriterionEvidenceObligations);
        Assert.Equal(CriterionEvidenceState.Pending, obligation.State);

        scene.Kernel.RephaseCriterionEvidenceObligation(scene.Goal.Id, obligation.Id,
            new CriterionObservationScope(CriterionObservationKind.Runtime, "observe deployment", "runtime"),
            "fixture operator", "observe after landing");
        if (reconstruct) scene.Reconstruct();
        Assert.Null(scene.Driver.TryHoldRepeatSoloAcceptance(scene.Goal, ConductorAutonomyPolicy.Conservative));
        scene.Advance(expectedGates: 1);

        Assert.NotEqual(historical.AttemptId, scene.Receipt.AttemptId);
        Assert.Equal(scene.CurrentMain, scene.Receipt.MainHeadSha);
        Assert.NotEqual(historical.MainHeadSha, scene.Receipt.MainHeadSha);
        var retained = JsonSerializer.Deserialize<ConductorParallelAcceptanceAttempt>(
            File.ReadAllText(historical.MetadataPath), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Null(retained.ReplayLandedAt);
        var duty = Assert.Single(scene.Goal.CriterionEvidenceObligations);
        Assert.Equal(CriterionEvidencePhase.PostLanding, duty.Phase);
        Assert.Equal(CriterionEvidenceState.Pending, duty.State);
    }

    [Xunit.Theory(Timeout = TestHangGuard.WholeTestMilliseconds)]
    [Xunit.Trait("Category", "CrossTick")]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public void WaivingOperatorBlockerRequiresGateForDisjointMain(bool reconstruct)
    {
        using var scene = new Scene(native: true, operatorObligation: true, occupyCapacity: false);
        scene.RecordOperatorEvidence(passed: false);
        scene.AdvanceMain("src/unrelated.cs");
        scene.Advance();
        Assert.Equal("criterion-evidence-admission/operator-prelanding-repeat-gate", scene.Goal.LandingBlocker!.Kind);
        var historical = scene.Receipt;
        var failedEvidence = JsonSerializer.Serialize(Assert.Single(scene.Goal.CriterionEvidenceObligations));

        scene.Kernel.WaiveAcceptanceCriterion(scene.Goal.Id, "1", "operator waived observation",
            currentCandidateSha: scene.CurrentCandidate);
        if (reconstruct) scene.Reconstruct();
        Assert.Empty(scene.Goal.GetLandingPrerequisiteObligations(scene.CurrentCandidate));
        Assert.Null(scene.Driver.TryHoldRepeatSoloAcceptance(scene.Goal, ConductorAutonomyPolicy.Conservative));
        scene.Advance(expectedGates: 1);

        Assert.NotEqual(historical.AttemptId, scene.Receipt.AttemptId);
        Assert.Equal(scene.CurrentMain, scene.Receipt.MainHeadSha);
        Assert.NotEqual(historical.MainHeadSha, scene.Receipt.MainHeadSha);
        var retained = JsonSerializer.Deserialize<ConductorParallelAcceptanceAttempt>(
            File.ReadAllText(historical.MetadataPath), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Null(retained.ReplayLandedAt);
        Assert.Equal(failedEvidence, JsonSerializer.Serialize(Assert.Single(scene.Goal.CriterionEvidenceObligations)));
    }

    [Xunit.Fact]
    public void UnreconciledAttemptCompletesBeforeRepeatHold()
    {
        using var scene = new Scene(operatorObligation: true, reconcileReceipt: false, occupyCapacity: false);
        Assert.Single(scene.Coordinator.GetUnreconciledAttempts([scene.Goal.Id.Value]));
        scene.Advance();
        Assert.Empty(scene.Coordinator.GetUnreconciledAttempts([scene.Goal.Id.Value]));
        Assert.NotNull(scene.Receipt.ReconciledAt);
        Assert.Equal(ConductorParallelAcceptanceAttemptOutcome.Passed, scene.Receipt.Outcome);
        Assert.NotEqual("criterion-evidence-admission/operator-prelanding-repeat-gate", scene.Goal.LandingBlocker!.Kind);
    }

    [Xunit.Fact(Timeout = TestHangGuard.WholeTestMilliseconds)]
    [Xunit.Trait("Category", "CrossTick")]
    public void ExistingLiveAttemptRunsAndReconcilesWithoutRepeatHold()
    {
        using var scene = new Scene(operatorObligation: true, occupyCapacity: false);
        var complete = scene.StartLiveAttempt();
        var live = Assert.Single(scene.Coordinator.GetUnreconciledAttempts([scene.Goal.Id.Value]));
        scene.Advance();
        Assert.Equal(live.AttemptId, scene.Receipt.AttemptId);
        Assert.Equal(ConductorParallelAcceptanceAttemptOutcome.Running, scene.Receipt.Outcome);
        Assert.Null(scene.Receipt.ReconciledAt);
        Assert.Single(scene.Coordinator.GetCapacityReservingAttempts([scene.Goal.Id.Value]));

        complete();
        scene.Advance();
        Assert.Equal(live.AttemptId, scene.Receipt.AttemptId);
        Assert.Equal(ConductorParallelAcceptanceAttemptOutcome.Passed, scene.Receipt.Outcome);
        Assert.NotNull(scene.Receipt.ReconciledAt);
        Assert.Empty(scene.Coordinator.GetCapacityReservingAttempts([scene.Goal.Id.Value]));
    }

    [Xunit.Fact(Timeout = TestHangGuard.WholeTestMilliseconds)]
    [Xunit.Trait("Category", "CrossTick")]
    public void ResolvingOneOfMultipleOperatorBlockersKeepsHold()
    {
        using var scene = new Scene(operatorObligation: true, operatorCount: 2);
        scene.Advance();
        scene.RecordOperatorEvidence(passed: true);
        scene.Advance();
        var remaining = Assert.Single(scene.Goal.GetLandingPrerequisiteObligations(scene.Candidate));
        Assert.Equal(1, remaining.CriterionIndex);
        Assert.Equal("criterion-evidence-admission/operator-prelanding-repeat-gate", scene.Goal.LandingBlocker!.Kind);
        Assert.Contains(remaining.Id, scene.Goal.LandingBlocker.Detail);
    }

    private sealed class Scene : IDisposable
    {
        public string Root { get; }
        public AgentOrchestratorKernel Kernel { get; private set; }
        public Goal Goal { get; private set; }
        public ConductorDriver Driver { get; private set; }
        public string Candidate { get; }
        public string Main { get; }
        public string CurrentCandidate { get; set; }
        public string CurrentMain { get; private set; }
        public IReadOnlyList<string> Scopes { get; set; } = ["src/receipt.txt"];
        public int Landings { get; private set; }
        private int _gates;
        public int Gates => _gates;
        private int _workers;
        private bool _liveHolder = true;
        private readonly IDisposable _census;
        private ConductorParallelAcceptanceAttemptCoordinator _coordinator;
        public ConductorParallelAcceptanceAttemptCoordinator Coordinator => _coordinator;
        private readonly ConductorParallelAcceptanceAttempt? _occupant;
        private readonly bool _native;
        private readonly string? _receiptPath;
        public ConductorParallelAcceptanceAttempt Receipt => _coordinator.TryReadLatest(Goal.Id.Value)!;

        public Scene(bool native = false, bool seedReceipt = true, bool operatorObligation = false,
            bool postLanding = false, bool occupyCapacity = true, bool reconcileReceipt = true, int operatorCount = 1,
            string? operatorCandidate = null, bool blockMutationLease = false)
        {
            _native = native;
            Root = native ? LandingExecutorTests.CreateGitRepository() : CreateTempDirectory("mcg-receipt-admission");
            if (native)
            {
                var seeded = LandingExecutorTests.CreateAcceptedCandidate(Root, "src/receipt.txt");
                Kernel = seeded.Kernel;
                Goal = seeded.Goal;
                Candidate = LandingExecutorTests.ReadGit(Root, "rev-parse", GoalWorktrees.BranchName(Goal.Id));
                Main = LandingExecutorTests.ReadGit(Root, "rev-parse", "main");
            }
            else
            {
                Kernel = new AgentOrchestratorKernel();
                Goal = CreateVerifiedSimpleGoal(Kernel, "Passed receipt admission ordering");
                Candidate = new string('a', 40);
                Main = new string('b', 40);
            }
            CurrentCandidate = Candidate;
            CurrentMain = Main;
            string[] criteria = operatorCount == 2 ? ["Required behavior", "Second behavior"] : ["Required behavior"];
            Kernel.RecordGoalRefinement(Goal.Id, new RefinedSpec(Goal.Objective,
                criteria, VerificationClass.TestVerifiable, [], [])
                { OperatorOwnedAcceptanceCriteria = operatorObligation ? criteria : [] });
            var obligation = Kernel.MapCriterionEvidenceOwner(Goal.Id, 0, 1,
                operatorObligation ? CriterionEvidenceOwner.Operator : CriterionEvidenceOwner.Acceptance,
                "fixture", operatorObligation ? "operator observation" : CriterionEvidenceScopes.FullAcceptanceGate,
                expectedCandidateSha: operatorObligation ? operatorCandidate ?? Candidate : Candidate);
            if (!operatorObligation)
                Kernel.RecordCriterionEvidence(Goal.Id, obligation.Id, obligation.Owner, Candidate,
                    "passing-gate", obligation.RequiredScope, true, "retained passed evidence");
            if (postLanding)
                Kernel.RephaseCriterionEvidenceObligation(Goal.Id, obligation.Id,
                    new CriterionObservationScope(CriterionObservationKind.Runtime, "observe deployment", "runtime"),
                    "fixture operator", "observe after landing");
            if (operatorCount == 2)
                Kernel.MapCriterionEvidenceOwner(Goal.Id, 1, 1, CriterionEvidenceOwner.Operator,
                    "fixture", "operator observation", expectedCandidateSha: operatorCandidate ?? Candidate);
            // Held receipts compare the effective manifest before replay. Supply the
            // same explicit home for capture and reconstruction without dirtying a worktree.
            var manifestHome = Path.Combine(Root, ".orchestrator");
            Directory.CreateDirectory(manifestHome);
            File.WriteAllText(Path.Combine(manifestHome, "acceptance-manifest.json"), """
                { "version": 1, "checks": [{ "name": "check", "type": "command", "command": "git", "arguments": ["diff", "--check"] }] }
                """);
            var attempts = Path.Combine(Root, ".orchestrator", "acceptance-gate-attempts");
            _coordinator = new ConductorParallelAcceptanceAttemptCoordinator(attempts, TrunkBranchName.Default,
                executionDirectory: Root, runInline: true, isProcessAlive: IsProcessAlive,
                acquireStableSlotLease: (_, _) => null) { ProjectHomeDirectory = manifestHome };
            if (seedReceipt)
            {
                var trx = Path.Combine(Root, "full-pass.trx");
                File.WriteAllText(trx, """
                    <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
                      <Results><UnitTestResult testId="1" testName="Passes" outcome="Passed" /></Results>
                      <ResultSummary outcome="Completed"><Counters total="1" executed="1" passed="1" failed="0" /></ResultSummary>
                    </TestRun>
                    """);
                var seed = _coordinator.Evaluate(ConductorParallelAcceptanceCandidate.Create(Goal, 0, Scopes, Candidate, Main),
                    ConductorAutonomyPolicy.Conservative, (candidate, _, _, _, _) =>
                        ConductorParallelAcceptanceRunResult.Accepted(candidate, new AcceptanceVerificationSummary(true, [],
                            TestResultPaths: [trx], BranchHeadSha: Candidate, MainHeadSha: Main)));
                Assert.Equal(ConductorParallelAcceptanceAttemptDecisionKind.Completed, seed.Kind);
                if (reconcileReceipt) _coordinator.MarkReconciled(seed.Attempt);
                Assert.NotNull(seed.Attempt.EffectiveManifestIdentity);
                _receiptPath = seed.Attempt.MetadataPath;
            }
            _census = GateLoadContextProbe.PushLiveGateOccupantProbe(() => []);
            if (occupyCapacity)
            {
                var otherGoal = CreateVerifiedSimpleGoal(Kernel, "Unrelated live gate");
                var other = new ConductorParallelAcceptanceAttemptCoordinator(attempts, TrunkBranchName.Default,
                    isProcessAlive: _ => true,
                    launchOwnedProcess: _ => new ConductorParallelAcceptanceOwnedProcessLaunchResult(9876));
                var started = other.Evaluate(ConductorParallelAcceptanceCandidate.Create(otherGoal, 0,
                    ["src/occupant.cs"], Candidate, Main), ConductorAutonomyPolicy.Conservative,
                    (_, _, _, _, _) => throw new InvalidOperationException("Occupant must stay live."));
                Assert.Equal(ConductorParallelAcceptanceAttemptDecisionKind.Started, started.Kind);
                _occupant = started.Attempt;
            }
            Driver = CreateDriver(blockMutationLease);
        }

        private ConductorDriver CreateDriver(bool blockMutationLease = false)
        {
            var driver = MakeDriver(getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
                dispatchAndStart: _ => { _workers++; return DispatchStartOutcome.Started(); },
                runAcceptanceWithSlot: (_, _) =>
                {
                    _gates++;
                    return new AcceptanceVerificationSummary(true, [], BranchHeadSha: CurrentCandidate, MainHeadSha: CurrentMain);
                },
                rebaseOntoMain: goal =>
                {
                    if (!_native) return DefaultRebaseSuccess();
                    var result = GoalWorktrees.TryRebaseOntoMain(Root, goal.Id);
                    CurrentCandidate = LandingExecutorTests.ReadGit(Root, "rev-parse", GoalWorktrees.BranchName(goal.Id));
                    return result;
                },
                land: goal =>
                {
                    Landings++;
                    return new LandingResult(goal.Id.Value, goal.Id.Value[..8], new LandingDecision.Promote(),
                        "integration", true, "Landed");
                }, getLandingFileScopes: _ => Scopes, parallelAcceptanceAttemptCoordinator: _coordinator,
                resolveAcceptanceHeads: _ => (CurrentCandidate, CurrentMain), executionDirectory: Root,
                tryAcquireEvidenceMutationLease: blockMutationLease ? (_, _) => null : null);
            driver.OverrideOwnerReviewHoldForTests(
                CollaborationItemStore.ForDirectory(Path.Combine(Root, ".orchestrator")),
                (_, _) => { }, (_, _) => "review", Kernel);
            return driver;
        }

        public void Reconstruct()
        {
            var id = Goal.Id;
            Kernel = AgentOrchestratorKernel.FromSnapshot(JsonSerializer.Deserialize<OrchestratorSnapshot>(
                JsonSerializer.Serialize(Kernel.ExportSnapshot()))!);
            Goal = Kernel.GetGoal(id);
            _coordinator = new ConductorParallelAcceptanceAttemptCoordinator(
                Path.Combine(Root, ".orchestrator", "acceptance-gate-attempts"), TrunkBranchName.Default,
                executionDirectory: Root, runInline: true, isProcessAlive: IsProcessAlive,
                acquireStableSlotLease: (_, _) => null) { ProjectHomeDirectory = Path.Combine(Root, ".orchestrator") };
            Driver = CreateDriver();
        }

        public void RecordOperatorEvidence(bool passed, string? candidate = null)
        {
            var obligation = Goal.CriterionEvidenceObligations.Single(item => item.CriterionIndex == 0);
            Kernel.RecordCriterionEvidence(Goal.Id, obligation.Id, CriterionEvidenceOwner.Operator,
                candidate ?? CurrentCandidate, "operator-" + Guid.NewGuid().ToString("N"), obligation.RequiredScope,
                passed, "fixture operator observation");
        }

        public Action StartLiveAttempt()
        {
            var paths = Receipt.TestResultPaths;
            CurrentMain = new string('e', 40);
            ConductorParallelAcceptanceOwnedProcessLaunch? launch = null;
            var background = new ConductorParallelAcceptanceAttemptCoordinator(
                Path.Combine(Root, ".orchestrator", "acceptance-gate-attempts"), TrunkBranchName.Default,
                executionDirectory: Root, isProcessAlive: IsProcessAlive, launchOwnedProcess: owned =>
                {
                    launch = owned;
                    return new ConductorParallelAcceptanceOwnedProcessLaunchResult(9877);
                }) { ProjectHomeDirectory = Path.Combine(Root, ".orchestrator") };
            var started = background.Evaluate(ConductorParallelAcceptanceCandidate.Create(Goal, 0, Scopes,
                CurrentCandidate, CurrentMain), ConductorAutonomyPolicy.Conservative,
                (candidate, _, _, _, _) => ConductorParallelAcceptanceRunResult.Accepted(candidate,
                    new AcceptanceVerificationSummary(true, [], TestResultPaths: paths,
                        BranchHeadSha: candidate.BranchHeadSha, MainHeadSha: candidate.MainHeadSha)));
            Assert.Equal(ConductorParallelAcceptanceAttemptDecisionKind.Started, started.Kind);
            Assert.NotNull(launch);
            return () =>
            {
                launch.ExecuteInCurrentProcess(9877);
                _liveHolder = false;
            };
        }

        private bool IsProcessAlive(int pid) => pid != 9877 || _liveHolder;

        public void RewriteReceipt(ConductorParallelAcceptanceAttempt attempt) =>
            File.WriteAllText(attempt.MetadataPath, JsonSerializer.Serialize(attempt, new JsonSerializerOptions(JsonSerializerDefaults.Web)));

        public void HoldLanding(bool apparatus)
        {
            var check = apparatus ? "acceptance apparatus" : "owner-protected configuration";
            Kernel.RecordAcceptanceFailure(Goal.Id, [check], CurrentCandidate, CurrentMain,
                checkAttributions: apparatus ? [new AcceptanceCheckAttribution(check,
                    AcceptanceFailureOrigin.Unattributed, "fixture apparatus failure", AcceptanceFailureCause.EnvironmentalApparatus)] : null);
            if (!apparatus)
                GoalOperationJournal.OwnerReviewHoldEntered(Root, Goal, CurrentCandidate, CurrentMain,
                    new OwnerReviewHoldReceipt("review", Goal.LatestAcceptanceFailure!.OccurredAt, "Owner approval required"),
                    ["owner-protected configuration"]);
        }

        public void AdvanceMain(string path)
        {
            var fullPath = Path.Combine(Root, path);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllText(fullPath, "main advanced");
            InfrastructureTestSupport.RunGitWithCommitPostcondition(Root, ["add", path]);
            InfrastructureTestSupport.RunGitWithCommitPostcondition(Root, ["commit", "-m", "Advance main"]);
            CurrentMain = LandingExecutorTests.ReadGit(Root, "rev-parse", "main");
            Assert.NotEqual(Main, CurrentMain);
        }

        public BatchTickSummary Advance(int expectedGates = 0, int width = 1)
        {
            if (_occupant is not null) Assert.Single(_coordinator.GetCapacityReservingAttempts([_occupant.GoalId]));
            BatchTickSummary? tick = null;
            new ConductorBatchLoop().Run(Kernel, Driver,
                ConductorAutonomyPolicy.Conservative with { AcceptanceWidth = width }, NoStopPath(),
                maxIterations: 1, onlyGoalId: Goal.Id.Value, onTick: current => tick = current);
            Assert.NotNull(tick);
            Assert.Equal(expectedGates, _gates);
            Assert.Equal(0, _workers);
            if (_occupant is not null)
            {
                var occupant = Assert.Single(_coordinator.GetCapacityReservingAttempts([_occupant.GoalId]));
                Assert.Equal(_occupant.AttemptId, occupant.AttemptId);
                Assert.Equal(_occupant.Outcome, occupant.Outcome);
                Assert.Null(occupant.ReconciledAt);
            }
            return tick;
        }

        // Goal progress emits reasons as tokens: spaces are replaced by underscores.
        // Check the emitted form in both arms so absence cannot pass vacuously.
        public void AssertNoCapacityHold(BatchTickSummary tick) =>
            Assert.DoesNotContain(tick.ProgressLines!, line => line.Contains("acceptance_width", StringComparison.Ordinal));

        public bool IsGoalHold(string line) =>
            line.StartsWith($"GOAL goal={Goal.Id.Value[..8]} result=held ", StringComparison.Ordinal);

        public void AssertCapacityHold(BatchTickSummary tick)
        {
            Assert.Contains(tick.ProgressLines!, line => IsGoalHold(line) &&
                line.Contains("reason=acceptance_width_1_reached;", StringComparison.Ordinal));
            Assert.Equal(0, Landings);
            if (_receiptPath is not null) Assert.Null(Receipt.ReplayLandedAt);
        }

        public void Dispose()
        {
            _census.Dispose();
            TryDeleteDirectory(Root);
        }
    }
}
