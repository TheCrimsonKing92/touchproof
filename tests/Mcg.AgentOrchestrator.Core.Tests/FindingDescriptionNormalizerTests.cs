using Mcg.AgentOrchestrator.Core;

public sealed class FindingDescriptionNormalizerTests
{
    [Xunit.Theory]
    [Xunit.InlineData("candidate abc123def has 23 failures", "candidate def456abc has 32 failures")]
    [Xunit.InlineData("Inspect src/Old.cs now.", "Inspect src/New.cs now.")]
    [Xunit.InlineData("Inspect C:\\repo\\Old.cs now.", "Inspect D:\\other\\New.cs now.")]
    [Xunit.InlineData("Observed 2026-10-30T12:34:56Z.", "Observed 2026-11-01T01:02:03Z.")]
    [Xunit.InlineData("  Cause\r\n evidence\t missing. ", "Cause evidence missing.")]
    public void Volatile_tokens_and_whitespace_do_not_change_description(string first, string second)
    {
        Assert.Equal(FindingDescriptionNormalizer.Normalize(first), FindingDescriptionNormalizer.Normalize(second));
    }

    [Xunit.Fact]
    public void Masks_whole_tokens_without_leaving_components()
    {
        Assert.Equal("At <timestamp>, <path> candidate <sha> has <number> failures.",
            FindingDescriptionNormalizer.Normalize(
                "At 2026-10-30T12:34:56Z, src/Cause.cs candidate abc123def has 23 failures."));
    }

    [Xunit.Theory]
    [Xunit.InlineData("Cause evidence is missing.", "Cause evidence is contradictory.")]
    [Xunit.InlineData("Cause evidence is missing.", "cause evidence is missing.")]
    [Xunit.InlineData("Cause evidence is missing.", "Cause evidence is missing!")]
    public void Wording_case_and_punctuation_remain_significant(string first, string second)
    {
        Assert.NotEqual(FindingDescriptionNormalizer.Normalize(first), FindingDescriptionNormalizer.Normalize(second));
    }
}
