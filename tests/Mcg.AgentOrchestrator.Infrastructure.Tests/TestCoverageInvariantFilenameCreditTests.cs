using Xunit;

namespace Mcg.AgentOrchestrator.Infrastructure.Tests;

// Parallel-safe: discovery identities and source mappings are supplied in memory.
public sealed class TestCoverageInvariantFilenameCreditTests
{
    [Theory]
    [InlineData(".json", 0)]
    [InlineData(".cs", 1)]
    public void RemovedFileWithMatchingStemCreditsOnlyCSharpWithoutLocations(string extension, int expectedCredit)
    {
        const string retained = "Example.Tests.RetainedTests.Runs";
        const string missing = "Example.Tests.MatchingTests.Runs";

        var result = TestCoverageInvariant.Evaluate(
            [retained], [], [retained, missing], ["tests/Example.Tests/MatchingTests" + extension]);

        Assert.Equal(new TestCoverageCountComparison(
            CandidateCount: 1,
            MinimumCandidateCount: 2 - expectedCredit,
            MainCount: 2,
            DeletedCount: expectedCredit,
            IsShortfall: expectedCredit == 0),
            result.CountComparison);
    }

    [Fact]
    public void RemovedCSharpFileCreditsExactlyTheCasesAtItsStructuredLocation()
    {
        const string removedPath = "tests/Example.Tests/MatchingTests.cs";
        const string retained = "Example.Tests.MatchingTests.Runs";
        const string removedFirst = "Example.Tests.OtherTests.First";
        const string removedSecond = "Example.Tests.OtherTests.Second";
        var sourceFiles = new Dictionary<string, string>
        {
            [retained] = "tests/Example.Tests/RetainedTests.cs",
            [removedFirst] = removedPath,
            [removedSecond] = removedPath,
        };

        var result = TestCoverageInvariant.Evaluate(
            [retained], [], [retained, removedFirst, removedSecond], [removedPath],
            mainDiscoveredTestSourceFiles: sourceFiles);

        Assert.Equal(new TestCoverageCountComparison(
            CandidateCount: 1, MinimumCandidateCount: 1, MainCount: 3, DeletedCount: 2, IsShortfall: false),
            result.CountComparison);
    }
}
