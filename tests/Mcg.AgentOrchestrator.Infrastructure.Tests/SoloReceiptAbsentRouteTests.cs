using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;

// Parallel-safe: in-memory goals and unique temporary TRX files.
public sealed class SoloReceiptAbsentRouteTests
{
    [Fact]
    public void ReadableAttemptTrx_RoutesIdentitiesAndFirstMessageLines()
    {
        using var fixture = new SoloCorrectionTestFixture();
        fixture.Record(origin: AcceptanceFailureOrigin.Unattributed);
        fixture.Trx("Tests.Alpha", "Tests.Beta");
        Assert.IsType<ConductorAdvanceOutcome.Executed>(fixture.Route().Outcome);
        var route = Assert.Single(fixture.Routes);
        Assert.StartsWith("ATTRIBUTED_FAILURE_ROUTE source=solo ", route.Feedback);
        Assert.Contains("failing_tests=Tests.Alpha,Tests.Beta", route.Feedback);
        Assert.Contains("failed Tests.Alpha: message 0", route.Feedback);
        Assert.Contains("failed Tests.Beta: message 1", route.Feedback);
        Assert.DoesNotContain("second line must stay out", route.Feedback);
        Assert.Contains("baseline attribution", route.Feedback);
        Assert.Equal(1, fixture.AttemptReads);
        Assert.Equal(1, fixture.RetryCalls);
        Assert.Equal(1, fixture.Goal.AutomaticAcceptanceRetryCount);
        Assert.Empty(fixture.Holds);
    }

    [Theory]
    [InlineData("absent")]
    [InlineData("malformed")]
    [InlineData("foreign-attempt")]
    [InlineData("foreign-candidate")]
    public void UnreadableTrx_OneFreshGateThenMappedHoldForSameCandidateAcrossRestart(string fault)
    {
        using var fixture = new SoloCorrectionTestFixture { AllowRoute = false };
        fixture.Record();
        fixture.Trx("Tests.Fails");
        if (fault == "absent") fixture.Attempt = null;
        if (fault == "malformed") File.WriteAllText(Path.Combine(fixture.Root, "failed.trx"), "not xml");
        if (fault == "foreign-attempt") fixture.Attempt = fixture.Attempt! with { AttemptId = "other" };
        if (fault == "foreign-candidate") fixture.Attempt = fixture.Attempt! with { BranchHeadSha = "other" };
        Assert.Equal("FreshGate", Assert.IsType<ConductorAdvanceOutcome.Executed>(fixture.Route().Outcome).Decision?.Action);
        Assert.Equal(GoalStatus.Verified, fixture.Goal.Status);
        Assert.Null(fixture.Goal.LatestAcceptanceFailure);
        fixture.Restart();
        fixture.Record();
        var held = Assert.IsType<ConductorAdvanceOutcome.Held>(fixture.Route().Outcome);
        Assert.Equal(SoloHoldReasons.ReceiptAbsent, held.Decision?.DiscriminatingEvidence);
        Assert.Contains("owner: operator", held.Reason);
        Assert.DoesNotContain("unmapped", held.Reason);
        Assert.Equal(GoalStatus.AcceptanceFailed, fixture.Goal.Status);
        Assert.Equal(0, fixture.RetryCalls);
        Assert.Equal(0, fixture.Goal.AutomaticAcceptanceRetryCount);
        Assert.Single(fixture.Holds);
        fixture.Context = fixture.Context with { CandidateSha = "next-candidate" };
        Assert.IsType<ConductorAdvanceOutcome.Executed>(fixture.Route().Outcome);
        Assert.Equal(GoalStatus.Verified, fixture.Goal.Status);
    }
}
