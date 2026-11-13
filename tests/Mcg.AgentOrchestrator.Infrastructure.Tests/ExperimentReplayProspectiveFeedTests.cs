using Mcg.AgentOrchestrator.App.Orchestration;
using Xunit;
using static ExperimentReplayTestData;

// Parallel-safe: offers use immutable fixtures and privately owned in-memory feed/rule pairs.
public sealed class ExperimentReplayProspectiveFeedTests
{
    [Fact]
    public void Offer_ValidPair_CountsBMinusACpuDifference()
    {
        var rule = new ExperimentReplaySequentialStopRule(0.05, 1, 5);
        var control = new ExperimentReplaySequentialStopRule(0.05, 1, 5);
        var feed = new ExperimentReplayProspectiveFeed(rule);
        var a = Execution(new(0, "case", "AB", 0, "A", false), 100);
        var b = Execution(new(1, "case", "AB", 0, "B", false), 120);
        Assert.True(ExperimentReplayStatistics.Valid(a));
        Assert.True(ExperimentReplayStatistics.Valid(b));

        var result = feed.Offer(a, b);
        Assert.True(result.Admitted);
        Assert.Equal(control.Observe(20), result.Decision);
        Assert.Equal(20, result.Decision!.Estimate);
        Assert.Equal(1, rule.Count);
        Assert.Empty(result.Rejected);
    }

    [Theory]
    [InlineData("A")]
    [InlineData("B")]
    public void Offer_InvalidMeasurement_RejectsWithoutCountingOrReplacing(string invalidArm)
    {
        var rule = new ExperimentReplaySequentialStopRule(0.05, 1, 5);
        var feed = new ExperimentReplayProspectiveFeed(rule);
        var a = Execution(new(0, "case", "AB", 0, "A", false), 100);
        var b = Execution(new(1, "case", "AB", 0, "B", false), 120);
        var admitted = feed.Offer(a, b);
        var invalidA = invalidArm == "A" ? a with { Resources = null } : a;
        var invalidB = invalidArm == "B" ? b with { Resources = null } : b;
        Assert.False(ExperimentReplayStatistics.Valid(invalidArm == "A" ? invalidA : invalidB));

        var rejected = feed.Offer(invalidA, invalidB);
        Assert.False(rejected.Admitted);
        Assert.Equal(admitted.Decision, rejected.Decision);
        Assert.Equal(1, rule.Count);
        var entry = Assert.Single(rejected.Rejected);
        Assert.Equal("case", entry.CaseId);
        Assert.Equal("invalid-measurement", entry.Reason);
        Assert.Equal(invalidA, entry.ExecutionA);
        Assert.Equal(invalidB, entry.ExecutionB);

        var next = feed.Offer(a, b);
        Assert.True(next.Admitted);
        Assert.Equal(2, rule.Count);
        Assert.Equal(entry, Assert.Single(next.Rejected));
        var again = feed.Offer(invalidA, invalidB);
        Assert.False(again.Admitted);
        Assert.Equal(2, rule.Count);
        Assert.Equal(new[] { entry, entry }, again.Rejected);
    }

    [Fact]
    public void Offer_CaseIdsDifferOrdinally_RejectsBeforeFirstObservation()
    {
        var rule = new ExperimentReplaySequentialStopRule(0.05, 1, 5);
        var feed = new ExperimentReplayProspectiveFeed(rule);
        var a = Execution(new(0, "case", "AB", 0, "A", false));
        var b = Execution(new(1, "CASE", "AB", 0, "B", false));
        Assert.True(ExperimentReplayStatistics.Valid(a));
        Assert.True(ExperimentReplayStatistics.Valid(b));
        var result = feed.Offer(a, b);
        Assert.False(result.Admitted);
        Assert.Equal("case-mismatch", Assert.Single(result.Rejected).Reason);
        Assert.Null(result.Decision);
        Assert.Equal(0, rule.Count);
    }

    [Theory]
    [InlineData("B", "A")]
    [InlineData("A", "A")]
    [InlineData("B", "B")]
    public void Offer_WrongArms_RejectsWithoutObserving(string armA, string armB)
    {
        var rule = new ExperimentReplaySequentialStopRule(0.05, 1, 5);
        var feed = new ExperimentReplayProspectiveFeed(rule);
        var a = Execution(new(0, "case", "AB", 0, armA, false));
        var b = Execution(new(1, "case", "AB", 0, armB, false));
        Assert.True(ExperimentReplayStatistics.Valid(a));
        Assert.True(ExperimentReplayStatistics.Valid(b));
        var result = feed.Offer(a, b);
        Assert.False(result.Admitted);
        Assert.Equal("arm-mismatch", Assert.Single(result.Rejected).Reason);
        Assert.Null(result.Decision);
        Assert.Equal(0, rule.Count);
    }

    [Fact]
    public void Offer_NullDependencies_ThrowsWithoutObserving()
    {
        Assert.Throws<ArgumentNullException>(() => new ExperimentReplayProspectiveFeed(null!));
        var rule = new ExperimentReplaySequentialStopRule(0.05, 1, 5);
        var feed = new ExperimentReplayProspectiveFeed(rule);
        var a = Execution(new(0, "case", "AB", 0, "A", false));
        var b = Execution(new(1, "case", "AB", 0, "B", false));
        Assert.Throws<ArgumentNullException>(() => feed.Offer(null!, b));
        Assert.Throws<ArgumentNullException>(() => feed.Offer(a, null!));
        Assert.Equal(0, rule.Count);
    }
}
