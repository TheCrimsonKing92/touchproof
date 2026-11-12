using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core.Conductor;

// Parallel-safe: in-memory goals and unique temporary TRX files.
public sealed class SoloCorrectionRetryBudgetTests
{
    [Fact]
    public void CappedFirstSignature_DifferentSignatureStillRoutes()
    {
        using var fixture = new SoloCorrectionTestFixture();
        Correct(fixture, "Tests.First", "second-candidate");
        Correct(fixture, "Tests.First", "third-candidate");
        fixture.Record();
        fixture.Trx("Tests.Second");
        Assert.IsType<ConductorAdvanceOutcome.Executed>(fixture.Route().Outcome);
        Assert.Equal(3, fixture.Routes.Count);
        Assert.Equal(3, fixture.RetryCalls);
        Assert.Equal(3, fixture.Goal.AutomaticAcceptanceRetryCount);
        Assert.Empty(fixture.Holds);
    }

    [Fact]
    public void CappedSignature_RepeatAcrossCandidatesHoldsWithoutChargingRetry()
    {
        using var fixture = new SoloCorrectionTestFixture();
        Correct(fixture, "Tests.First", "second-candidate");
        Correct(fixture, "Tests.First", "third-candidate");
        fixture.Restart();
        fixture.Record();
        fixture.Trx("Tests.First");
        var held = Assert.IsType<ConductorAdvanceOutcome.Held>(fixture.Route().Outcome);
        Assert.Equal(SoloHoldReasons.RetryCap, held.Decision?.DiscriminatingEvidence);
        Assert.Contains("owner: operator", held.Reason);
        Assert.Equal(2, fixture.Routes.Count);
        Assert.Equal(2, fixture.RetryCalls);
        Assert.Equal(2, fixture.Goal.AutomaticAcceptanceRetryCount);
        Assert.Single(fixture.Holds);
    }

    [Fact]
    public void OverallCeiling_HoldsNewSignatureWithOperatorAndEvidence()
    {
        using var fixture = new SoloCorrectionTestFixture();
        for (var i = 0; i < 4; i++) Correct(fixture, $"Tests.Failure{i}", $"candidate-{i}");
        fixture.Record();
        fixture.Trx("Tests.Unseen");
        var held = Assert.IsType<ConductorAdvanceOutcome.Held>(fixture.Route().Outcome);
        Assert.Equal(SoloHoldReasons.CorrectionCeiling, held.Decision?.DiscriminatingEvidence);
        Assert.Contains("owner: operator", held.Reason);
        Assert.Contains("attempt=attempt", held.Reason);
        Assert.Contains("evidence=absent", held.Reason);
        Assert.Equal(4, fixture.Routes.Count);
        Assert.Equal(4, fixture.RetryCalls);
        Assert.Equal(4, fixture.Goal.AutomaticAcceptanceRetryCount);
        Assert.Single(fixture.Holds);
    }

    [Fact]
    public void SignatureUsesWholeSortedSetIncludingTheoryCommas()
    {
        using var fixture = new SoloCorrectionTestFixture();
        fixture.Record();
        fixture.Trx("Tests.Theory(a: 1, b: 2)", "Tests.Other");
        fixture.Route();
        fixture.CompleteCorrection("second");
        fixture.Record();
        fixture.Trx("Tests.Other", "Tests.Theory(a: 1, b: 2)");
        fixture.Route();
        fixture.CompleteCorrection("third");
        fixture.Record();
        fixture.Trx("Tests.Theory(a: 1, b: 2)", "Tests.Other");
        var held = Assert.IsType<ConductorAdvanceOutcome.Held>(fixture.Route().Outcome);
        Assert.Equal(SoloHoldReasons.RetryCap, held.Decision?.DiscriminatingEvidence);
        Assert.Equal(2, fixture.Routes.Count);
    }

    private static void Correct(SoloCorrectionTestFixture fixture, string test, string nextCandidate)
    {
        fixture.Record();
        fixture.Trx(test);
        Assert.IsType<ConductorAdvanceOutcome.Executed>(fixture.Route().Outcome);
        fixture.CompleteCorrection(nextCandidate);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    public void OperatorRegate_AfterCapOrCeiling_RestoresCorrectionBudget(int priorCount)
    {
        using var fixture = new SoloCorrectionTestFixture();
        for (var i = 0; i < priorCount; i++)
            Correct(fixture, priorCount == 2 ? "Tests.First" : $"Tests.Failure{i}", $"candidate-{i}");
        fixture.Record();
        fixture.Trx(priorCount == 2 ? "Tests.First" : "Tests.Unseen");
        var held = Assert.IsType<ConductorAdvanceOutcome.Held>(fixture.Route().Outcome);
        Assert.Equal(priorCount == 2 ? SoloHoldReasons.RetryCap : SoloHoldReasons.CorrectionCeiling,
            held.Decision?.DiscriminatingEvidence);

        fixture.Kernel.RetryAcceptanceGate(fixture.Goal.Id, "operator reviewed the evidence");
        fixture.Restart();
        Assert.Equal(0, fixture.Goal.AutomaticAcceptanceRetryCount);
        fixture.Record();
        fixture.Trx("Tests.First");
        Assert.IsType<ConductorAdvanceOutcome.Executed>(fixture.Route().Outcome);
        Assert.Equal(priorCount + 1, fixture.Routes.Count);
        Assert.Equal(1, fixture.Goal.AutomaticAcceptanceRetryCount);
    }
}
