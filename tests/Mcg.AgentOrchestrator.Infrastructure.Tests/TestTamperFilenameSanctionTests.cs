using Xunit;

namespace Mcg.AgentOrchestrator.Infrastructure.Tests;

// Parallel-safe: the runner supplies a synthetic diff and does not use git or the filesystem.
public sealed class TestTamperFilenameSanctionTests
{
    [Theory]
    [InlineData(".json", "Example.Tests.MatchingTests.Removed", false)]
    [InlineData(".cs", "Example.Tests.MatchingTests.Removed", true)]
    [InlineData(".cs", "Example.Tests.MatchingTests.DifferentMethod", false)]
    public async Task SanctionMatchesFilenameFallbackOnlyForCSharp(
        string extension, string sanctionedRemoval, bool expectedPassed)
    {
        var path = "tests/Example.Tests/MatchingTests" + extension;
        var diff = $"""
            diff --git a/{path} b/{path}
            --- a/{path}
            +++ b/{path}
            @@ -11,2 +10,0 @@
            -    [Fact]
            -    public void Removed() => Assert.Equal(1, GetValue());
            """;

        var result = await TestTamperAnalysis.RunTestTamperCheckAsync(
            (args, _, _, _) =>
            {
                Assert.Equal(["git", "diff", "--unified=0", "main...HEAD"], args);
                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, diff));
            },
            new AcceptanceGateEngineSettings(),
            "unused-worktree",
            "main",
            [sanctionedRemoval],
            TestContext.Current.CancellationToken);

        Assert.Equal(expectedPassed, result.Passed);
        if (!expectedPassed)
        {
            Assert.Contains("1 test method(s) removed", result.OutputTail);
            Assert.Contains(path, result.OutputTail);
        }
    }
}
