using Mcg.AgentOrchestrator.Core;

// Parallel-safe: pure attribution and digest inputs, with an injected clock.
public sealed class OwnerDigestRevertAttributionNegativeControlTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 16, 0, 0, 0, TimeSpan.Zero);
    private static readonly string IntroducedSha = new('a', 40);
    private static readonly string FirstParentSha = new('b', 40);
    private static readonly string UnknownSha = new('c', 40);

    [Fact]
    public void Attribute_UnattributedReverts_CountWithoutEscapingLanding()
    {
        // First-parent ancestry is deliberately absent from the introduced set;
        // OwnerDigestRevertReaderTests proves that exclusion against real git.
        var attribution = OwnerDigestRevertAttribution.Attribute(
            [new("parent-revert", $"This reverts commit {FirstParentSha}."),
             new("unknown-revert", $"This reverts commit {UnknownSha}."),
             new("no-line-revert", "Revert feature without a canonical attribution line")],
            [new("goal", [IntroducedSha])]);

        Assert.Equal(3, attribution.Count);
        Assert.Collection(attribution.Matches,
            match => AssertUnattributed(match, "parent-revert", FirstParentSha),
            match => AssertUnattributed(match, "unknown-revert", UnknownSha),
            match => AssertUnattributed(match, "no-line-revert", null));
        var digest = Build(attribution);
        Assert.Equal("3", digest.Reverts);
        var row = Assert.Single(digest.Goals);
        Assert.Equal("correct", row.LandingStatus);
        Assert.Null(row.EscapeSource);
        Assert.Equal(0, digest.Totals.Escapes);
        Assert.Equal(1, digest.Totals.CorrectLandings);
    }

    [Theory]
    [InlineData(39)]
    [InlineData(41)]
    public void Attribute_NonFullSha_CountsWithoutAttribution(int length)
    {
        var attribution = OwnerDigestRevertAttribution.Attribute(
            [new("invalid-sha", "This reverts commit " + new string('a', length) + ".")],
            [new("goal", [IntroducedSha])]);

        Assert.Equal(1, attribution.Count);
        AssertUnattributed(Assert.Single(attribution.Matches), "invalid-sha", null);
    }

    [Fact]
    public void Build_RecordAndRepeatedReverts_CountOneEscape()
    {
        var attribution = OwnerDigestRevertAttribution.Attribute(
            [new("first", $"THIS REVERTS COMMIT {IntroducedSha.ToUpperInvariant()}."),
             new("second", $"This reverts commit {IntroducedSha}.")],
            [new("goal", [IntroducedSha])]);
        Assert.Equal(2, attribution.Count);
        Assert.Equal(2, attribution.Matches.Count);
        Assert.All(attribution.Matches, match => Assert.Equal("goal", match.GoalId));

        var digest = Build(attribution,
            [new("goal", null, Start.AddHours(3), "Recorded defect")]);

        Assert.Equal("2", digest.Reverts);
        var row = Assert.Single(digest.Goals);
        Assert.Equal("escape", row.LandingStatus);
        Assert.Equal("record,revert", row.EscapeSource);
        Assert.Single(digest.Escapes!);
        Assert.Equal(1, digest.Totals.Escapes);
        Assert.Equal(0, digest.Totals.CorrectLandings);
        Assert.Equal(0.0, digest.Totals.CorrectLandingRate);
    }

    private static void AssertUnattributed(OwnerDigestRevertMatch match, string revertSha,
        string? revertedSha)
    {
        Assert.Equal(revertSha, match.RevertSha);
        Assert.Equal(revertedSha, match.RevertedSha);
        Assert.Null(match.GoalId);
    }

    private static OwnerDigestResult Build(OwnerDigestRevertAttribution attribution,
        IReadOnlyList<OwnerDigestEscapeRecord>? records = null) => OwnerDigestReport.Build(
        [new("goal", Start.AddHours(1), "landing", [])],
        [new("landing", Start.AddHours(2), true)],
        new FixedClock(Start.AddDays(1)), Start, Start.AddDays(1),
        escapeRecords: records, revertAttribution: attribution);

    private sealed record FixedClock(DateTimeOffset UtcNow) : IClock;
}
