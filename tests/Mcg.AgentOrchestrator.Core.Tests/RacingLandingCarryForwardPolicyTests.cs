using Mcg.AgentOrchestrator.Core;

public sealed class RacingLandingCarryForwardPolicyTests
{
    private static readonly RacingLandingCarryForwardFacts Overlap =
        new("Overlap", "ExactPath", "src/A.cs", "src/A.cs", "", "overlap receipt");
    private static readonly RacingLandingCarryForwardFacts Unknown =
        new("Unknown", "", "", "", "git-changed-paths-unavailable", "unknown receipt");

    [Theory]
    [InlineData("Overlap", 1, "main-advance-overlap",
        "Main advanced with overlapping changes; revalidation required before landing. overlap receipt")]
    [InlineData("Unknown", 2, "main-advance-unknown",
        "Acceptance candidate relationship could not be classified; revalidation required before landing. unknown receipt")]
    public void DecisionRecordsDistinctEvidenceAndRoundTripsFacts(string disposition, int rung, string evidence, string reason)
    {
        var facts = disposition == "Overlap" ? Overlap : Unknown;
        var decision = RacingLandingCarryForwardPolicy.Evaluate(facts);
        var recorded = decision.ToRecord();

        Assert.Equal("racing-landing-carry-forward", recorded.Stage);
        Assert.Equal("Hold", recorded.Action);
        Assert.Equal(rung, recorded.Rung);
        Assert.Equal(evidence, recorded.DiscriminatingEvidence);
        Assert.Equal(reason, recorded.Reason);
        Assert.Equal(disposition, Assert.Single(recorded.Facts, fact => fact.Name == "disposition").Value);
        Assert.Equal(facts, RacingLandingCarryForwardFacts.FromRecordedFacts(recorded.Facts));
        var replayed = RacingLandingCarryForwardFacts.FromRecordedFacts(recorded.Facts.Reverse().ToArray());
        Assert.Equal(decision, RacingLandingCarryForwardPolicy.Evaluate(replayed));
    }

    [Fact]
    public void ReplayRejectsNullFacts() =>
        Assert.Throws<ArgumentNullException>(() => RacingLandingCarryForwardFacts.FromRecordedFacts(null!));

    [Fact]
    public void ReplayRejectsWrongFactCount()
    {
        var facts = Overlap.ToRecordedFacts();
        const string expected = "Racing landing carry-forward replay requires exactly six named facts.";
        Assert.Equal(expected, Assert.Throws<InvalidOperationException>(() =>
            RacingLandingCarryForwardFacts.FromRecordedFacts(facts.Skip(1).ToArray())).Message);
        Assert.Equal(expected, Assert.Throws<InvalidOperationException>(() =>
            RacingLandingCarryForwardFacts.FromRecordedFacts([.. facts, new("extra", "")])).Message);
    }

    [Fact]
    public void ReplayRejectsDuplicateFactName()
    {
        var facts = Overlap.ToRecordedFacts().ToArray();
        facts[1] = facts[0];
        var error = Assert.Throws<InvalidOperationException>(() => RacingLandingCarryForwardFacts.FromRecordedFacts(facts));
        Assert.Equal("Duplicate racing landing carry-forward fact 'disposition'.", error.Message);
    }

    [Fact]
    public void ReplayRejectsMissingFactNameAtExpectedCount()
    {
        var facts = Overlap.ToRecordedFacts().ToArray();
        facts[0] = new("unrecognized", facts[0].Value);
        var error = Assert.Throws<InvalidOperationException>(() => RacingLandingCarryForwardFacts.FromRecordedFacts(facts));
        Assert.Equal("Missing racing landing carry-forward fact 'disposition'.", error.Message);
    }

    [Theory]
    [InlineData("Unchanged")]
    [InlineData("Disjoint")]
    [InlineData("overlap")]
    [InlineData("invalid")]
    public void ReplayAndEvaluationRejectUnsupportedDisposition(string disposition)
    {
        var facts = Overlap with { Disposition = disposition };
        Assert.Throws<InvalidOperationException>(() => RacingLandingCarryForwardFacts.FromRecordedFacts(facts.ToRecordedFacts()));
        Assert.Throws<InvalidOperationException>(() => RacingLandingCarryForwardPolicy.Evaluate(facts));
    }
}
