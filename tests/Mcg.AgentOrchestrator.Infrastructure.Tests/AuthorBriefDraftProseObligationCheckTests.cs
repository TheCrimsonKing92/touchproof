using Mcg.AgentOrchestrator.App.Orchestration;

// Parallel-safe: immutable markdown and an in-memory repository; no filesystem or process access.
public sealed class AuthorBriefDraftProseObligationCheckTests
{
    private const string TesterSentence = "The Tester's evidence_request runs the deferred tests.";
    private const string OwnerSentence = "Integration owner: the Developer; evidence owners: the Tester and the Reviewer.";
    private const string GateSentence = "The normal broad gate remains required after the focused verification.";
    private const string OperatorSentence = "After the goal lands, the Operator confirms the receipt.";

    [Theory]
    [InlineData(TesterSentence, "Tester evidence_request")]
    [InlineData(OwnerSentence, "integration/evidence owner")]
    [InlineData(GateSentence, "normal/broad gate remains required")]
    [InlineData(OperatorSentence, "after-landing operator")]
    public void Run_ProseShape_FailsWithShapeSentenceAndRemedy(string sentence, string shape)
    {
        var checks = Run(InScope(sentence));
        var failed = Assert.Single(checks, check => !check.Passed);

        Assert.Equal("prose-obligations", failed.Name);
        Assert.Contains($"{shape}: \"{sentence}\"", failed.Detail);
        Assert.Contains("Remove each quoted sentence or move its content into a numbered criterion ending with an owner sentence.", failed.Detail);
    }

    [Theory]
    [InlineData("The TESTER's evidence-request executes the deferred tests.", "Tester evidence_request")]
    [InlineData("The TESTER's evidence request RUNS the deferred tests.", "Tester evidence_request")]
    [InlineData("Evidence owners are Developer, Tester and Reviewer as numbered above; integration is through the normal acceptance gate.", "integration/evidence owner")]
    [InlineData("The Developer owns integration.", "integration/evidence owner")]
    [InlineData("The Tester owns the evidence.", "integration/evidence owner")]
    [InlineData("Evidence-owner: the Reviewer.", "integration/evidence owner")]
    [InlineData("The broad-gate is still required.", "normal/broad gate remains required")]
    [InlineData("The normal gate stays required.", "normal/broad gate remains required")]
    [InlineData("After this lands, the operator confirms the receipt.", "after-landing operator")]
    [InlineData("Once this goal has landed, the operator records the result.", "after-landing operator")]
    [InlineData("Post-landing, the operator records the result.", "after-landing operator")]
    public void Run_WordingVariant_FailsWithStableShape(string sentence, string shape)
    {
        var check = Prose(InScope(sentence));

        Assert.False(check.Passed);
        Assert.Contains(shape, check.Detail);
        Assert.Contains($"\"{sentence}\"", check.Detail);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Run_CleanOrHouseCriterion_Passes(bool includeHouseCriterion)
    {
        var markdown = CliAuthorDraftCommandTests.ValidMarkdown.ReplaceLineEndings("\n");
        if (!includeHouseCriterion)
            markdown = string.Join("\n", markdown.Split('\n').Where(line => !line.StartsWith("2. ", StringComparison.Ordinal)));

        var check = Prose(markdown);

        Assert.True(check.Passed, check.Detail);
        Assert.Equal("No owner, gate or after-landing sentence was found outside the numbered acceptance criteria.", check.Detail);
    }

    [Theory]
    [InlineData(TesterSentence)]
    [InlineData(OwnerSentence)]
    [InlineData(GateSentence)]
    [InlineData(OperatorSentence)]
    public void Run_NumberedAcceptanceLine_ExemptsEveryShape(string sentence)
    {
        var markdown = CliAuthorDraftCommandTests.ValidMarkdown.Replace("1. The receipt records the result.", "1. " + sentence);

        var check = Prose(markdown);

        Assert.True(check.Passed, check.Detail);
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void Run_ContinuationLine_ScansInsideAcceptanceCriteria(string newline)
    {
        var markdown = CliAuthorDraftCommandTests.ValidMarkdown.Replace("1. The receipt", "   " + TesterSentence + "\n1. The receipt")
            .ReplaceLineEndings(newline);

        var check = Prose(markdown);

        Assert.False(check.Passed);
        Assert.Contains($"Tester evidence_request: \"{TesterSentence}\"", check.Detail);
    }

    [Theory]
    [InlineData("1. ")]
    [InlineData("1) ")]
    [InlineData("- ")]
    public void Run_NumberedOrBulletedScopeLine_IsScanned(string prefix)
    {
        var check = Prose(InScope(prefix + TesterSentence));

        Assert.False(check.Passed);
        Assert.Contains("Tester evidence_request", check.Detail);
    }

    [Theory]
    [InlineData("## acceptance CRITERIA", true)]
    [InlineData("## Acceptance criteria (draft)", true)]
    [InlineData("## Scope", false)]
    [InlineData("# Another draft", false)]
    public void Run_SectionHeading_ControlsNumberedLineExemption(string heading, bool passes)
    {
        var check = Prose("## Acceptance criteria\n" + heading + "\n1) " + TesterSentence);

        Assert.Equal(passes, check.Passed);
        if (!passes) Assert.Contains("Tester evidence_request", check.Detail);
    }

    [Theory]
    [InlineData("```markdown", "```")]
    [InlineData("~~~markdown", "~~~")]
    [InlineData("````markdown", "````")]
    public void Run_FencedShapesAndHeadings_AreIgnored(string opening, string closing)
    {
        var fenced = $"{opening}\n## Acceptance criteria\n{TesterSentence}\n{OwnerSentence}\n{GateSentence}\n{OperatorSentence}\n{closing}";
        Assert.True(Prose(InScope(fenced)).Passed);

        var afterFence = Prose(InScope(fenced + "\n1. " + TesterSentence));
        Assert.False(afterFence.Passed);
        Assert.Contains("Tester evidence_request", afterFence.Detail);
    }

    [Fact]
    public void Run_MultipleMatches_QuotesEachSentenceAndShapeInSourceOrder()
    {
        const string combined = "The normal broad gate remains required; integration owner: Developer.";
        var check = Prose(InScope(TesterSentence + " Harmless sentence. " + OwnerSentence + "\n" + combined));

        Assert.False(check.Passed);
        Assert.StartsWith($"Tester evidence_request: \"{TesterSentence}\" | integration/evidence owner: \"{OwnerSentence}\" | " +
            $"integration/evidence owner, normal/broad gate remains required: \"{combined}\"", check.Detail);
        Assert.DoesNotContain("Harmless sentence.", check.Detail);
    }

    [Theory]
    [InlineData("Developer owns; Acceptance executes. TEST-VERIFIABLE.")]
    [InlineData("After landing, record the result.")]
    [InlineData("The acceptance gate runs the focused tests.")]
    [InlineData("After the change lands, the operator repairs the goal.")]
    [InlineData("The Tester reviews evidence_request output.")]
    [InlineData("The normal gate is available; evidence is recorded.")]
    [InlineData("The Tester reviews the draft. The evidence_request runs tests.")]
    [InlineData("The Tester reviews the draft;\nthe evidence_request runs tests.")]
    [InlineData("After the goal lands, record the receipt. The operator reads this draft.")]
    public void Run_UnrelatedProse_Passes(string prose)
    {
        var check = Prose(InScope(prose));

        Assert.True(check.Passed, check.Detail);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Plain text without headings.")]
    public void Run_EmptyOrHeadinglessText_PassesWithoutThrowing(string markdown)
    {
        Assert.True(Prose(markdown).Passed);
    }

    [Fact]
    public void Run_HeadinglessObligation_IsScanned()
    {
        Assert.False(Prose(TesterSentence).Passed);
    }

    private static string InScope(string prose) =>
        CliAuthorDraftCommandTests.ValidMarkdown.Replace("Drafting only.", prose);

    private static AuthorBriefDraftCheck Prose(string markdown) =>
        Assert.Single(Run(markdown), check => check.Name == "prose-obligations");

    private static IReadOnlyList<AuthorBriefDraftCheck> Run(string markdown) =>
        AuthorBriefDraftChecks.Run(markdown, CliAuthorDraftCommandTests.Fixture.MainSha,
            new CliAuthorDraftCommandTests.FakeRepository());
}
