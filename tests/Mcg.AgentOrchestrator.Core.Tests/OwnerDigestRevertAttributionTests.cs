using Mcg.AgentOrchestrator.Core;

public sealed class OwnerDigestRevertAttributionTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 16, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset End = Start.AddDays(1);
    private static readonly string IntroducedSha = new('a', 40);

    [Fact(DisplayName = "Two reverts reclassify one passing landing and lower the correct rate")]
    public void Build_AttributedReverts_ReclassifyPassingLanding()
    {
        var attribution = Attribute();
        var result = Build(attribution);

        Assert.Equal(2, attribution.Count);
        Assert.All(attribution.Matches, match => Assert.Equal("a", match.GoalId));
        Assert.Equal("revert-1", attribution.Matches[0].RevertSha);
        Assert.Equal(IntroducedSha.ToUpperInvariant(), attribution.Matches[0].RevertedSha);
        Assert.Equal("2", result.Reverts);
        Assert.Equal("escape", result.Goals[0].LandingStatus);
        Assert.Equal("revert", result.Goals[0].EscapeSource);
        Assert.Equal("correct", result.Goals[1].LandingStatus);
        Assert.Null(result.Goals[1].EscapeSource);
        Assert.Equal(1, result.Totals.Escapes);
        Assert.Equal(1, result.Totals.CorrectLandings);
        Assert.Equal(0.5, result.Totals.CorrectLandingRate);
    }

    [Theory(DisplayName = "Reverts join existing escape sources in a stable order")]
    [InlineData(false, false, "revert")]
    [InlineData(true, false, "canary,revert")]
    [InlineData(false, true, "record,revert")]
    [InlineData(true, true, "canary,record,revert")]
    public void Build_MultipleSources_CountsGoalOnce(bool canaryFailed, bool recorded, string source)
    {
        var result = Build(Attribute(), canaryFailed, recorded);

        Assert.Equal(source, result.Goals[0].EscapeSource);
        Assert.Equal("escape", result.Goals[0].LandingStatus);
        Assert.Equal(1, result.Totals.Escapes);
    }

    [Fact(DisplayName = "Available empty revert data reports zero instead of not tracked")]
    public void Build_EmptyRevertData_ReportsZero()
    {
        var result = Build(OwnerDigestRevertAttribution.Attribute([], []));

        Assert.Equal("0", result.Reverts);
        Assert.Equal(0, result.Totals.Escapes);
        Assert.Equal(1.0, result.Totals.CorrectLandingRate);
    }

    private static OwnerDigestRevertAttribution Attribute() => OwnerDigestRevertAttribution.Attribute(
        [new("revert-1", "Revert feature\n\nTHIS REVERTS COMMIT " + IntroducedSha.ToUpperInvariant() + ".\n"),
         new("revert-2", "This reverts commit " + IntroducedSha + ".")],
        [new("a", [IntroducedSha])]);

    private static OwnerDigestResult Build(OwnerDigestRevertAttribution attribution,
        bool canaryFailed = false, bool recorded = false) => OwnerDigestReport.Build(
        [new("a", Start.AddHours(1), "landing-a", []), new("b", Start.AddHours(2), "landing-b", [])],
        [new("landing-a", Start.AddHours(3), !canaryFailed), new("landing-b", Start.AddHours(4), true)],
        new FixedClock(End), Start, End,
        escapeRecords: recorded ? [new("a", null, Start.AddHours(5), "Defect")] : null,
        revertAttribution: attribution);

    private sealed record FixedClock(DateTimeOffset UtcNow) : IClock;
}
