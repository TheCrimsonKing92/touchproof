using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Xunit;

// Parallel-safe: pure rendering and validation with private in-memory goals.
public sealed class PullRequestBodySummaryTests
{
    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void Render_NumberedSummary_PreservesNestingAndSectionOrder(string newline)
    {
        string[] summaryLines =
        [
            "",
            "1. Replace the combat calculation.",
            "  - Keep the five expectations.",
            "   - Include the zero-damage case.",
            "",
            "2. Update the UI.",
            "  - Show the resulting damage.",
            "3. Document the behavior.",
            ""
        ];
        var objective = string.Join(newline, new[]
        {
            "Combat changes", "# Combat math coverage", "", "## What to build"
        }.Concat(summaryLines).Concat(["## Acceptance criteria", "1. Verify combat math."]));

        var body = Render(objective);

        Assert.Equal(string.Join(Environment.NewLine, new[]
        {
            "Combat changes", "Combat math coverage", "", "## What this changes", ""
        }.Concat(summaryLines).Concat(
        [
            "", "## Acceptance criteria", "", "1. Verify combat math.", "", "## Gate", "",
            "acceptance gate passed, attempt gate-attempt-1, checks: unit, lint"
        ])), body);
    }

    [Fact]
    public void Render_SummarySecrets_RedactsEachLineAndKeepsSurroundingText()
    {
        var body = Render(WithSummary("""
            1. Inspect `C:\Users\someone\x` and keep this explanation.
              - Verify SOMEONE output.
            2. Remove `ghp_example_access_string` from the example.
            3. Inspect .orchestrator/private
            """));

        Assert.Equal(string.Join(Environment.NewLine,
        [
            "1. Inspect `[redacted]` and keep this explanation.",
            "  - Verify [redacted] output.",
            "2. Remove `[redacted]` from the example.",
            "3. Inspect [redacted]"
        ]), SummaryContent(body));
        Assert.DoesNotContain("someone", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("C:\\Users", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(".orchestrator", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Render_LongSummary_TruncatesAtCompleteLineWithinLimit()
    {
        var sourceLines = Enumerable.Range(1, 100)
            .Select(index => $"{index:D3}. {new string('x', 44)}").ToArray();
        var source = string.Join("\n", sourceLines) + "\n";
        Assert.Equal(5000, source.Length);

        var summary = SummaryContent(Render(WithSummary(source)));

        Assert.True(summary.Length <= 3000);
        var renderedLines = summary.Split(Environment.NewLine);
        Assert.Equal("…", renderedLines[^1]);
        var retainedCount = renderedLines.Length - 1;
        Assert.InRange(retainedCount, 1, sourceLines.Length - 1);
        Assert.Equal(sourceLines.Take(retainedCount), renderedLines.Take(retainedCount));
        Assert.True(summary.Length + Environment.NewLine.Length + sourceLines[retainedCount].Length > 3000);
    }

    [Theory]
    [InlineData(2999)]
    [InlineData(3000)]
    [InlineData(3001)]
    [InlineData(5000)]
    public void Render_SingleSummaryLine_RespectsCapWithoutSplitting(int length)
    {
        var source = new string('x', length);

        var summary = SummaryContent(Render(WithSummary(source)));

        Assert.Equal(length <= 3000 ? source : "…", summary);
    }

    [Fact]
    public void Render_LongLocalPath_AppliesCapAfterRedaction()
    {
        var body = Render(WithSummary($"1. Inspect `C:\\Users\\someone\\{new string('x', 5000)}` safely."));

        Assert.Equal("1. Inspect `[redacted]` safely.", SummaryContent(body));
    }

    [Fact]
    public void Render_OversizedFirstLine_DoesNotSkipToLaterLines()
    {
        var body = Render(WithSummary(new string('x', 5000) + "\n2. This later line fits."));

        Assert.Equal("…", SummaryContent(body));
    }

    [Theory]
    [InlineData("")]
    [InlineData("# What to build\nDo not include this.")]
    [InlineData("### What to build\nDo not include this.")]
    [InlineData(" ## What to build\nDo not include this.")]
    [InlineData("## what to build\nDo not include this.")]
    public void Render_NoMatchingHeading_OmitsSummary(string section)
    {
        var body = Render($"# Combat coverage\n{section}\n## Acceptance criteria\n1. Verify combat math.");

        Assert.DoesNotContain("## What this changes", body);
        Assert.Contains("## Acceptance criteria", body);
        Assert.Contains("## Gate", body);
    }

    [Fact]
    public void Render_RepositoryPath_PreservesSummaryVerbatim()
    {
        const string source = "1. Update `src/game/combat-math.ts` and `src/game/combat-math.test.ts`.";

        var body = Render(WithSummary(source));

        Assert.Equal(source, SummaryContent(body));
        Assert.DoesNotContain("[redacted]", body);
    }

    [Fact]
    public void Render_MultipleSections_UsesFirstLiteralPrefixThroughNextH2()
    {
        var body = Render("""
            # Combat coverage
            ## What to build (details)
            1. Keep this item.
            ### Details
              - Keep this nested item.
            ## Scope
            Do not include this scope.
            ## What to build
            Do not include this second section.
            ## Acceptance criteria
            1. Verify combat math.
            """);

        Assert.Equal(string.Join(Environment.NewLine,
            ["1. Keep this item.", "### Details", "  - Keep this nested item."]), SummaryContent(body));
        Assert.DoesNotContain("Do not include", body);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Render_EmptySection_IncludesSummaryHeading(string content)
    {
        var body = Render(WithSummary(content));

        Assert.Contains("## What this changes", body);
        Assert.Equal("", SummaryContent(body));
    }

    [Fact]
    public void Render_HeadingsInsideFence_UsesLiteralLinePrefixes()
    {
        var body = Render("""
            # Combat coverage
            ```
            ## What to build
            1. Keep this item.
            ## Scope
            Do not include this scope.
            ```
            ## Acceptance criteria
            1. Verify combat math.
            """);

        Assert.Equal("1. Keep this item.", SummaryContent(body));
        Assert.DoesNotContain("Do not include", body);
    }

    [Fact]
    public void Render_SectionAtEnd_IncludesRemainingContent()
    {
        var body = Render("# Combat coverage\n## What to build\n1. Keep the final item.");

        Assert.Equal("1. Keep the final item.", SummaryContent(body));
    }

    [Fact]
    public void Render_ScrubbingExposesPathInSummary_RefusesWholeBody()
    {
        var goal = CreateGoal(WithSummary("1. Inspect CaseyC:/private/x.cs"));

        var error = Assert.Throws<InvalidOperationException>(() =>
            PullRequestBodyRenderer.Render(goal, "gate-attempt-1", ["unit"], "/home/Casey/private"));

        Assert.Equal("Pull request content could not be rendered safely.", error.Message);
    }

    private static string WithSummary(string content) =>
        $"# Combat coverage\n## What to build\n{content}\n## Acceptance criteria\n1. Verify combat math.";

    private static string Render(string objective) =>
        PullRequestBodyRenderer.Render(CreateGoal(objective), "gate-attempt-1", ["unit", "lint"]).Body;

    private static Goal CreateGoal(string objective) => new AgentOrchestratorKernel().CreateGoal(objective,
        [new TaskSpec(TaskId.New(), "Render pull request content", AgentRole.Developer)]);

    private static string SummaryContent(string body)
    {
        var start = body.IndexOf("## What this changes" + Environment.NewLine, StringComparison.Ordinal);
        Assert.True(start >= 0, "The summary heading must be present.");
        start += "## What this changes".Length + Environment.NewLine.Length;
        var end = body.IndexOf("## Acceptance criteria", start, StringComparison.Ordinal);
        Assert.True(end >= start, "Acceptance criteria must follow the summary.");
        return body[start..end].Trim('\r', '\n');
    }
}
