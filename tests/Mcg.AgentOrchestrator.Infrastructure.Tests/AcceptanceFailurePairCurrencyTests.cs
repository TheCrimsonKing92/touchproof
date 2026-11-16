using Mcg.AgentOrchestrator.App.Orchestration;

// Parallel-safe: pure comparisons with no external state.
public sealed class AcceptanceFailurePairCurrencyTests
{
    [Theory]
    [InlineData("branch", "main", "branch", "main", "Current")]
    [InlineData("BRANCH", "MAIN", "branch", "main", "Current")]
    [InlineData("old", "main", "branch", "main", "Superseded")]
    [InlineData("branch", "old", "branch", "main", "Superseded")]
    [InlineData("old-branch", "old-main", "branch", "main", "Superseded")]
    [InlineData(null, "main", "branch", "main", "Unknown")]
    [InlineData("", "main", "branch", "main", "Unknown")]
    [InlineData(" ", "main", "branch", "main", "Unknown")]
    [InlineData("branch", null, "branch", "main", "Unknown")]
    [InlineData("branch", "", "branch", "main", "Unknown")]
    [InlineData("branch", "\t", "branch", "main", "Unknown")]
    [InlineData("branch", "main", null, "main", "Unknown")]
    [InlineData("branch", "main", "", "main", "Unknown")]
    [InlineData("branch", "main", "\r\n", "main", "Unknown")]
    [InlineData("branch", "main", "branch", null, "Unknown")]
    [InlineData("branch", "main", "branch", "", "Unknown")]
    [InlineData("branch", "main", "branch", " ", "Unknown")]
    [InlineData(null, "old", "branch", "main", "Unknown")]
    [InlineData("old", null, "branch", "main", "Unknown")]
    [InlineData("branch", "old", null, "main", "Unknown")]
    [InlineData("old", "main", "branch", null, "Unknown")]
    public void Classify_CompleteOrMissingPair_ReturnsCurrency(string? recordedBranch,
        string? recordedMain, string? currentBranch, string? currentMain, string expected)
    {
        var actual = AcceptanceFailurePairCurrency.Classify(recordedBranch, recordedMain, currentBranch, currentMain);

        Assert.Equal(expected, actual.ToString());
    }
}
