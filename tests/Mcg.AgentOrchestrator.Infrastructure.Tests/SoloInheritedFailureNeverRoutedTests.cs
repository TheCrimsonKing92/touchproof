using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;

// Parallel-safe: in-memory goals and unique temporary TRX files.
public sealed class SoloInheritedFailureNeverRoutedTests
{
    [Fact]
    public void ReceiptAbsent_InheritedTrxNeverRoutesOrChargesRetry()
    {
        using var fixture = new SoloCorrectionTestFixture { AllowRoute = false };
        fixture.Record(origin: AcceptanceFailureOrigin.Inherited);
        fixture.Trx("Tests.Inherited");
        var held = Assert.IsType<ConductorAdvanceOutcome.Held>(fixture.Route().Outcome);
        Assert.Equal(SoloHoldReasons.ReceiptAbsentInherited, held.Decision?.DiscriminatingEvidence);
        Assert.Contains("owner: operator", held.Reason);
        Assert.Equal(0, fixture.AttemptReads);
        Assert.Equal(0, fixture.RetryCalls);
        Assert.Empty(fixture.Routes);
        Assert.Equal(0, fixture.Goal.AutomaticAcceptanceRetryCount);
    }

    [Fact]
    public void StaleReceipt_InheritedFailureOnlyAdmitsFreshGate()
    {
        using var fixture = new SoloCorrectionTestFixture { AllowRoute = false };
        fixture.Record(fixture.StaleReceipt(), AcceptanceFailureOrigin.Inherited);
        Assert.Equal("FreshGate", Assert.IsType<ConductorAdvanceOutcome.Executed>(fixture.Route().Outcome).Decision?.Action);
        Assert.Equal(GoalStatus.Verified, fixture.Goal.Status);
        Assert.Null(fixture.Goal.LatestAcceptanceFailure);
        Assert.Empty(fixture.Routes);
        Assert.Empty(fixture.Holds);
        Assert.Equal(0, fixture.RetryCalls);
    }
}
