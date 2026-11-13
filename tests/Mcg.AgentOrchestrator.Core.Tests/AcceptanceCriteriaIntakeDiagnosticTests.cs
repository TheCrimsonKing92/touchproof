using Mcg.AgentOrchestrator.Core;

public sealed class AcceptanceCriteriaIntakeDiagnosticTests
{
    [Theory(DisplayName = "An empty explicit section identifies its heading and repair")]
    [InlineData("Acceptance criteria")]
    [InlineData("Acceptance criteria:")]
    [InlineData("## Acceptance criteria")]
    [InlineData("## Acceptance")]
    public void EmptySectionReportsHeadingLineAndRepair(string heading)
    {
        var diagnostic = Assert.IsType<AcceptanceCriteriaIntakeDiagnostic>(
            AcceptanceCriteriaIntakeDiagnostic.Evaluate($"Build.\n\n{heading}\n\nOperating instructions\n- Keep context."));
        Assert.Equal(3, diagnostic.HeadingLineNumber);
        Assert.Equal(heading, diagnostic.HeadingText);
        Assert.Equal("empty-section", diagnostic.Kind);
        Assert.Equal($"Acceptance criteria empty-section at line 3 ({heading}): " +
            "add numbered or bulleted items directly under the heading, or remove the heading.", diagnostic.Message);
    }

    [Theory(DisplayName = "An unparseable explicit list reports a malformed section")]
    [InlineData("Unlisted prose.")]
    [InlineData("1.No separator\n-\n*")]
    public void MalformedSectionReportsRepair(string body)
    {
        var diagnostic = Assert.IsType<AcceptanceCriteriaIntakeDiagnostic>(
            AcceptanceCriteriaIntakeDiagnostic.Evaluate($"Acceptance criteria\n{body}"));
        Assert.Equal("malformed-section", diagnostic.Kind);
        Assert.Equal(1, diagnostic.HeadingLineNumber);
        Assert.Contains("add numbered or bulleted items", diagnostic.Message);
    }

    [Theory(DisplayName = "Freeform headings and valid lists need no intake diagnostic")]
    [InlineData("Freeform without a heading.")]
    [InlineData("## Acceptance considerations\nProse only.")]
    [InlineData("A sentence about Acceptance criteria:\n- Unrelated.")]
    [InlineData("- Acceptance criteria\n- Unrelated.")]
    [InlineData("Acceptance criteria\n- A valid criterion.")]
    [InlineData("Acceptance criteria\n- test-removal: Example.Tests.Removed")]
    public void AbsentBroadOrPopulatedSectionsHaveNoDiagnostic(string objective)
    {
        Assert.Null(AcceptanceCriteriaIntakeDiagnostic.Evaluate(objective));
    }
}
