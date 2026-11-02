using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;

// Parallel-safe: immutable fixture text and pure routing calls only.
public sealed class ConductorAuthorPartialCitationGroundingTests
{
    private const string First = "The conductor executes the requested test classes.";
    private const string Second = "The reviewer supplies the final diff evidence.";
    private const string Third = "The acceptance manifest lists dependency installation before compilation.";
    private const string Short = "Developer owns the tests";
    private const string Brief = First + "\n" + Second + "\n" + Third + "\n" + Short + ".";
    private const string Answer = First + " " + Second +
        " The manifest puts installing dependencies ahead of compiling.";
    private const string ParaphrasedAnswer =
        "Test execution belongs to the conductor, diff review belongs to the reviewer, " +
        "and installing dependencies precedes compiling in the manifest.";

    [Xunit.Fact]
    public void Two_quoted_citations_and_one_paraphrased_citation_ground_all_three()
    {
        var baseline = ConductorAuthorAnswerRouting.Decide(Item(), Answer, []);
        var route = ConductorAuthorAnswerRouting.Decide(Item(), Answer, [], Brief, [First, Second, Third]);

        Xunit.Assert.Equal("missing-evidence", baseline.EscalationReason);
        Xunit.Assert.Null(route.EscalationReason);
        Xunit.Assert.Equal(Answer, route.SubmittedText);
        Xunit.Assert.Equal(new[] { First, Second, Third }, route.GroundedCitations);
    }

    [Xunit.Fact]
    public void Extra_citation_outside_the_brief_keeps_missing_evidence()
    {
        const string extra = "The report includes a complete dependency inventory.";
        AssertMissingEvidence(Answer + " " + extra, [First, Second, Third, extra]);
    }

    [Xunit.Fact]
    public void No_citation_in_the_answer_keeps_missing_evidence()
    {
        AssertMissingEvidence(ParaphrasedAnswer, [First, Second, Third]);
    }

    [Xunit.Fact]
    public void Empty_citations_keep_missing_evidence()
    {
        AssertMissingEvidence(Answer, []);
    }

    [Xunit.Fact]
    public void Short_citation_among_valid_citations_keeps_missing_evidence()
    {
        AssertMissingEvidence(Answer + " " + Short, [First, Second, Third, Short]);
    }

    [Xunit.Fact]
    public void Partly_paraphrased_citations_do_not_cover_an_unnamed_file()
    {
        AssertMissingEvidence(Answer + " The implementation is in src/Example/Other.cs.",
            [First, Second, Third]);
    }

    [Xunit.Fact]
    public void Whitespace_normalizes_with_only_one_citation_in_the_answer()
    {
        var answer = First.Replace(" executes ", "\r\n\t executes  ", StringComparison.Ordinal) +
            " Diff evidence comes from review, and dependencies precede compiling.";
        var brief = Brief.Replace(" supplies ", "  supplies\r\n\t", StringComparison.Ordinal);
        var citations = new[] { First, Second, Third }
            .Select(citation => "\t" + citation.Replace(" ", "  \r\n\t", StringComparison.Ordinal) + "  ")
            .ToArray();

        var route = ConductorAuthorAnswerRouting.Decide(Item(), answer, [], brief, citations);

        Xunit.Assert.Null(route.EscalationReason);
        Xunit.Assert.Equal(answer, route.SubmittedText);
        Xunit.Assert.Equal(new[] { First, Second, Third }, route.GroundedCitations);
    }

    private static void AssertMissingEvidence(string answer, IReadOnlyList<string> citations)
    {
        var baseline = ConductorAuthorAnswerRouting.Decide(Item(), answer, []);
        var route = ConductorAuthorAnswerRouting.Decide(Item(), answer, [], Brief, citations);

        Xunit.Assert.Equal("missing-evidence", baseline.EscalationReason);
        Xunit.Assert.Equal(baseline.EscalationReason, route.EscalationReason);
        Xunit.Assert.Equal(answer, route.SubmittedText);
        Xunit.Assert.Null(route.GroundedCitations);
    }

    private static ConductorAuthorItem Item() =>
        new(OperatorAnswerTargetKind.Clarification, "c1", "g1",
            "Who executes tests and supplies review evidence?", "runtime");
}
