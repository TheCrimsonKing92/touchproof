// Parallel-safe: each fixture owns its repository, receipts and detached checkouts.
public sealed class ImpactReplayQualifiedSelectionDiagnosticTests
{
    [Fact]
    public void AmbiguityDiagnosticDoesNotWidenQualifiedPoolSelection()
    {
        using var fixture = new ImpactReplayFixture(changedSourceCount: 0, balancedPool: true,
            changedTestFiles: new Dictionary<string, string>
            {
                ["Nested/PoolAlphaTests.cs"] = """
                    namespace SampleTests.Nested;
                    public class PoolAlphaTests { [Xunit.Fact] public void Works() { } }
                    """
            });
        fixture.AddAttempt("qualified-pool");

        var (report, _) = fixture.Run();

        var attempt = Assert.Single(report.Attempts);
        Assert.Equal("Resolved", attempt.SelectionOutcome);
        Assert.Null(attempt.DegradationKind);
        Assert.Equal(new[] { "PoolAlphaTests" }, attempt.SelectedTestClasses);
        var diagnostic = Assert.Single(report.UnmappedTestClasses!.Occurrences);
        Assert.Equal("PoolAlphaTests", diagnostic.Class);
        Assert.Equal("ambiguous", diagnostic.Reason);
        Assert.Equal(new[] { "SampleTests.Nested.PoolAlphaTests", "SampleTests.PoolAlphaTests" }, diagnostic.FullNames);
        Assert.False(Assert.Single(attempt.BalancedClasses!, c => c.ClassName == "SampleTests.PoolAlphaTests").WouldRun);
        Assert.Equal(10d, attempt.PoolSecondsSaved);
    }
}
