using Mcg.AgentOrchestrator.App.Orchestration;

namespace Mcg.AgentOrchestrator.Infrastructure.Tests;

public sealed class BacklogBundleDetectorTests
{
    [Theory]
    [InlineData("  wAnTeD: (4) First\r\n2. Second\r\n8) Third\r\nConstraints:\r\n1. Outside")]
    [InlineData("### WANTED ###\n(4) First\n2. Second\n8) Third\n#### Constraints\n1. Outside")]
    [InlineData("Wanted\n======\n(4) First\n2. Second\n8) Third\nConstraints\n-----------\n1. Outside")]
    public void FindsOrderedPartsAcrossHeadingAndMarkerStyles(string body)
    {
        Assert.Equal(["First", "Second", "Third"], BacklogBundleDetector.FindParts(body));
    }

    [Fact]
    public void KeepsContinuationsAndNestedEntriesWithTheirParent()
    {
        const string body = """
            Wanted:
              1. First
                 More detail
                 (1) Nested step
              2. Second
            """;

        var parts = BacklogBundleDetector.FindParts(body);

        Assert.Equal(["First\n     More detail\n     (1) Nested step", "Second"], parts);
        Assert.Equal("1. First More detail (1) Nested step | 2. Second", BacklogBundleDetector.RenderParts(parts));
    }

    [Fact]
    public void IgnoresFencedHeadingsAndPartMarkers()
    {
        const string body = """
            ~~~markdown
            # Wanted
            1. Decoy
            2. Decoy
            ~~~
            Wanted:
            1. First
            ~~~markdown
            ## Constraints
            1. Example
            2. Example
            ~~~
            2. Second
            """;

        Assert.Equal(["First", "Second"], BacklogBundleDetector.FindParts(body));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Details:\n1. Outside\n2. Outside")]
    [InlineData("1. Outside\n2. Outside\nWanted:\n1. Only")]
    [InlineData("Wanted:\n1. Only\n2.\n3)")]
    [InlineData("Wanted:\n1. Only\n## Later\n1. Outside\n2. Outside")]
    [InlineData("Wanted:\n1. Only\nWanted: (1) Later first\n2. Later second")]
    [InlineData("Wanted: (1) One line with (2) a parenthesized reference")]
    public void ReturnsNoBundleWithoutTwoNonemptyTopLevelParts(string? body)
    {
        Assert.Empty(BacklogBundleDetector.FindParts(body));
    }
}
