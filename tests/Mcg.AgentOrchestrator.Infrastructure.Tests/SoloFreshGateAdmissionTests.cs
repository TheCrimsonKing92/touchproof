using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;

// Parallel-safe: in-memory goals and unique temporary artifacts.
public sealed class SoloFreshGateAdmissionTests
{
    [Fact]
    public void StaleReceipt_AdmitsCurrentCandidateWithoutHoldOrRetry()
    {
        using var fixture = new SoloCorrectionTestFixture { AllowRoute = false };
        fixture.Record(fixture.StaleReceipt());
        var result = fixture.Route();
        var executed = Assert.IsType<ConductorAdvanceOutcome.Executed>(result.Outcome);
        Assert.Equal("FreshGate", executed.Decision?.Action);
        Assert.Equal(SoloHoldReasons.RevisionMismatch, executed.Decision?.DiscriminatingEvidence);
        Assert.Contains(new PolicyDecisionFact("candidateSha", fixture.Context.CandidateSha), executed.Decision!.Facts);
        Assert.Equal(GoalStatus.Verified, fixture.Goal.Status);
        Assert.Null(fixture.Goal.LatestAcceptanceFailure);
        Assert.Null(fixture.Goal.RetainedAcceptanceFailure);
        Assert.Equal(0, fixture.Goal.AutomaticAcceptanceRetryCount);
        Assert.Equal(0, fixture.RetryCalls);
        Assert.Equal(0, fixture.AttemptReads);
        Assert.Empty(fixture.Holds);
        Assert.Empty(fixture.Routes);
        Assert.Single(fixture.Goal.Timeline.Where(evt => evt.Message.StartsWith("SOLO_FRESH_GATE ", StringComparison.Ordinal)));
    }

    [Fact]
    public void TrxForPreviousCandidate_AdmitsCurrentCandidateWithoutRoutingOldFailures()
    {
        using var fixture = new SoloCorrectionTestFixture { AllowRoute = false };
        fixture.Record();
        fixture.Trx("Tests.OldFailure");
        fixture.Context = fixture.Context with { CandidateSha = "current-candidate" };
        var result = fixture.Route();
        Assert.Equal(SoloHoldReasons.CandidateMismatch,
            Assert.IsType<ConductorAdvanceOutcome.Executed>(result.Outcome).Decision?.DiscriminatingEvidence);
        Assert.Equal(GoalStatus.Verified, fixture.Goal.Status);
        Assert.Null(fixture.Goal.LatestAcceptanceFailure);
        Assert.Equal(0, fixture.RetryCalls);
        Assert.Empty(fixture.Holds);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RepeatedMismatch_SameRevisionsHold_ChangedMainAdmits(bool receipt)
    {
        using var fixture = new SoloCorrectionTestFixture { AllowRoute = false };
        var current = fixture.Context;
        void RecordMismatch()
        {
            fixture.Context = current with { CandidateSha = "old-candidate" };
            fixture.Record(receipt ? fixture.StaleReceipt() with { CandidateSha = "older-candidate" } : null);
            fixture.Trx("Tests.OldFailure");
            fixture.Context = current;
        }
        RecordMismatch();
        Assert.Equal("FreshGate", Assert.IsType<ConductorAdvanceOutcome.Executed>(fixture.Route().Outcome).Decision?.Action);
        fixture.Restart();
        RecordMismatch();
        var held = Assert.IsType<ConductorAdvanceOutcome.Held>(fixture.Route().Outcome);
        Assert.Equal(receipt ? SoloHoldReasons.RevisionMismatch : SoloHoldReasons.CandidateMismatch,
            held.Decision?.DiscriminatingEvidence);
        Assert.Contains("owner: operator", held.Reason);
        Assert.NotNull(fixture.Goal.LatestAcceptanceFailure);
        fixture.Context = current with { MainSha = "advanced-main" };
        Assert.Equal("FreshGate", Assert.IsType<ConductorAdvanceOutcome.Executed>(fixture.Route().Outcome).Decision?.Action);
        Assert.Equal(GoalStatus.Verified, fixture.Goal.Status);
        Assert.Equal(0, fixture.RetryCalls);
    }

    [Fact]
    public void StaleGoalInstance_DoesNotClearCanonicalGoalEvidence()
    {
        using var fixture = new SoloCorrectionTestFixture();
        fixture.Record(fixture.StaleReceipt());
        var stale = fixture.Goal;
        fixture.Restart();
        fixture.Kernel.RetryAcceptanceGate(fixture.Goal.Id, "operator reviewed");
        fixture.Kernel.RecordAcceptanceFailure(fixture.Goal.Id, ["new failure"]);
        var failure = fixture.Goal.LatestAcceptanceFailure;
        Assert.NotNull(failure);
        Assert.Null(SoloFreshGateAdmission.TryAdmit(fixture.Kernel, stale, stale.Id.Value[..8],
            fixture.Policy, fixture.Context, SoloHoldReasons.RevisionMismatch));
        Assert.Same(failure, fixture.Goal.LatestAcceptanceFailure);
        Assert.Equal(GoalStatus.Verified, fixture.Goal.Status);
    }

    [Fact]
    public void UnavailableFreshGate_NamesOperatorAndResolvingAction()
    {
        var next = SoloHoldNextAction.For(SoloHoldReasons.FreshGateUnavailable);
        Assert.Equal("operator", next.Owner);
        Assert.Contains("incomplete tasks", next.NextAction);
        Assert.Contains("re-gate", next.NextAction);
    }
}
