using Mcg.AgentOrchestrator.App.Orchestration;

// Parallel-safe: checks use a per-test fake repository; no files, processes or shared state.
public sealed class AuthorBriefDraftProvidedPremiseTests
{
    private const string MissingCitation = "missing/dependency.cs:1";

    [Fact]
    public void Run_UnmarkedUntrackedCitation_FailsPremiseCitations()
    {
        var repository = new CliAuthorDraftCommandTests.FakeRepository();
        Assert.Null(repository.TrackedLineCount(CliAuthorDraftCommandTests.Fixture.MainSha,
            "missing/dependency.cs"));

        var checks = AuthorBriefDraftChecks.Run(WithPremise($"- Observed in `{MissingCitation}`."),
            CliAuthorDraftCommandTests.Fixture.MainSha, repository, dependencyGoals: ["abcdef"]);

        var failure = Assert.Single(checks.Where(check => !check.Passed));
        Assert.Equal("premise-citations", failure.Name);
        Assert.Contains(MissingCitation, failure.Detail);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("fedcba")]
    [InlineData("abcdef0")]
    public void Run_UnlistedProvidedGoal_FailsProvidedByGoals(string? listedGoal)
    {
        var markdown = WithPremise($"- provided-by-abcdef: Supplies `{MissingCitation}`.");
        var repository = new CliAuthorDraftCommandTests.FakeRepository();
        var checks = listedGoal is null
            ? AuthorBriefDraftChecks.Run(markdown, CliAuthorDraftCommandTests.Fixture.MainSha, repository)
            : AuthorBriefDraftChecks.Run(markdown, CliAuthorDraftCommandTests.Fixture.MainSha, repository,
                dependencyGoals: [listedGoal]);

        var failure = Assert.Single(checks.Where(check => !check.Passed));
        Assert.Equal("provided-by-goals", failure.Name);
        Assert.Contains("abcdef", failure.Detail);
        Assert.Contains("dependsOnGoals", failure.Detail);
        Assert.True(Assert.Single(checks, check => check.Name == "premise-citations").Passed);
    }

    [Theory]
    [InlineData("Observed provided-by-abcdef:")]
    [InlineData("Provided-by-abcdef:")]
    [InlineData("provided-by-abcde:")]
    [InlineData("provided-by-abcdef0123456789abcdef01234567890:")]
    [InlineData("provided-by-abcdeg:")]
    [InlineData("provided-by-abcdef")]
    public void Run_InvalidMarker_RetainsCitationValidation(string marker)
    {
        var checks = AuthorBriefDraftChecks.Run(WithPremise($"- {marker} `{MissingCitation}`."),
            CliAuthorDraftCommandTests.Fixture.MainSha, new CliAuthorDraftCommandTests.FakeRepository(),
            dependencyGoals: ["abcdef"]);

        var failure = Assert.Single(checks.Where(check => !check.Passed));
        Assert.Equal("premise-citations", failure.Name);
        Assert.Contains(MissingCitation, failure.Detail);
        Assert.True(Assert.Single(checks, check => check.Name == "provided-by-goals").Passed);
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void Run_ParagraphAfterBlankLine_RetainsCitationValidation(string newline)
    {
        var markdown = WithPremise("- provided-by-abcdef: Supplies `missing/provided.cs:1`.\n\n" +
            $"Observed separately in `{MissingCitation}`.").ReplaceLineEndings(newline);

        var checks = AuthorBriefDraftChecks.Run(markdown, CliAuthorDraftCommandTests.Fixture.MainSha,
            new CliAuthorDraftCommandTests.FakeRepository(), dependencyGoals: ["abcdef"]);

        var failure = Assert.Single(checks.Where(check => !check.Passed));
        Assert.Equal("premise-citations", failure.Name);
        Assert.Contains(MissingCitation, failure.Detail);
        Assert.DoesNotContain("missing/provided.cs", failure.Detail);
    }

    [Fact]
    public void Run_GoalDiffersOnlyByHexCase_PassesProvidedByGoals()
    {
        var checks = AuthorBriefDraftChecks.Run(
            WithPremise($"- provided-by-ABCdef: Supplies `{MissingCitation}`."),
            CliAuthorDraftCommandTests.Fixture.MainSha, new CliAuthorDraftCommandTests.FakeRepository(),
            dependencyGoals: ["abcDEF"]);

        Assert.NotEmpty(checks);
        Assert.All(checks, check => Assert.True(check.Passed, check.Detail));
        Assert.True(Assert.Single(checks, check => check.Name == "provided-by-goals").Passed);
        Assert.Equal("1 premise bullets.", Assert.Single(checks, check => check.Name == "premise-bullets").Detail);
    }

    private static string WithPremise(string premise)
    {
        const string original = "- Observed in `docs/role-capability-matrix.md:1`.";
        Assert.Contains(original, CliAuthorDraftCommandTests.ValidMarkdown);
        return CliAuthorDraftCommandTests.ValidMarkdown.Replace(original, premise);
    }
}
