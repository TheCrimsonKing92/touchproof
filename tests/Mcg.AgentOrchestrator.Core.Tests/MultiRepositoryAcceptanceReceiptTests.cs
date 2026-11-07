using System.Text.Json;
using Mcg.AgentOrchestrator.Core;

// Parallel-safe: immutable in-memory receipts only.
public sealed class MultiRepositoryAcceptanceReceiptTests
{
    private static RepositoryCandidateTuple Tuple(string secondaryPatch = "secondary") => new([
        new("default", new CandidateIdentity("home", "base", "manifest")),
        new("crosswalk", new CandidateIdentity(secondaryPatch, "base", "manifest"))]);

    [Fact]
    public void RoundTrip_CompleteReceipt_PreservesVerdictAndCandidate()
    {
        var outcomes = new List<RepositoryAcceptanceOutcome> {
            new("default", true, "home-evidence", null), new("crosswalk", true, "secondary-evidence", null) };
        var receipt = new MultiRepositoryAcceptanceReceipt(GoalId.New(), Tuple().Canonical, outcomes, DateTimeOffset.UtcNow);
        outcomes.Clear();
        var restored = JsonSerializer.Deserialize<MultiRepositoryAcceptanceReceipt>(JsonSerializer.Serialize(receipt))!;
        Assert.True(restored.Passed);
        Assert.True(restored.IsComplete(new GoalRepositorySet(["default", "crosswalk"])));
        Assert.Equal(new[] { "crosswalk", "default" }, restored.Outcomes.Select(outcome => outcome.Key));
        Assert.True(restored.IsCurrentFor(Tuple()));
        Assert.False(restored.IsCurrentFor(Tuple("changed")));
        Assert.False(restored.IsComplete(new GoalRepositorySet(["default"])));
    }

    [Fact]
    public void Passed_OneRepositoryFailed_ReportsFailedKey()
    {
        var receipt = new MultiRepositoryAcceptanceReceipt(GoalId.New(), Tuple().Canonical,
            [new("default", true, "home-evidence", null), new("crosswalk", false, "secondary-evidence", "failed")], DateTimeOffset.UtcNow);
        Assert.False(receipt.Passed);
        Assert.Equal(new[] { "crosswalk" }, receipt.FailedKeys);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("extra")]
    [InlineData("no-evidence")]
    public void Construct_IncompleteOrAmbiguousEvidence_RejectsReceipt(string defect)
    {
        var outcomes = new List<RepositoryAcceptanceOutcome> {
            new("default", true, "home-evidence", null),
            new("crosswalk", true, defect == "no-evidence" ? "" : "secondary-evidence", null) };
        if (defect == "missing") outcomes.RemoveAt(0);
        if (defect == "duplicate") outcomes.Add(outcomes[0]);
        if (defect == "extra") outcomes.Add(new("other", true, "evidence", null));
        Assert.Throws<ArgumentException>(() => new MultiRepositoryAcceptanceReceipt(GoalId.New(), Tuple().Canonical, outcomes, DateTimeOffset.UtcNow));
    }
}
