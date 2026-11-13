using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each fixture owns its temporary workspace/database; model and git are fake.
public sealed class AuthorBriefDraftPremiseBulletsTests
{
    [Theory]
    [InlineData("prose", false)]
    [InlineData("dash", true)]
    [InlineData("asterisk", true)]
    [InlineData("missing", false)]
    [InlineData("empty", false)]
    [InlineData("numbered", false)]
    [InlineData("mixed", true)]
    [InlineData("crlf", true)]
    public void Run_PremiseShape_RequiresRecognizedBullet(string shape, bool expected)
    {
        using var fixture = new CliAuthorDraftCommandTests.Fixture();
        var valid = CliAuthorDraftCommandTests.ValidMarkdown;
        var markdown = shape switch
        {
            "prose" => Bulletless(valid),
            "asterisk" => valid.Replace("- Observed", "* Observed"),
            "missing" => valid.Replace("## Measured premise", "## Other section"),
            "empty" => valid.Replace("- Observed in `docs/role-capability-matrix.md:1`.", ""),
            "numbered" => valid.Replace("- Observed", "1. Observed"),
            "mixed" => valid.Replace("- Observed", "Runtime result is unknown.\n- Observed"),
            "crlf" => valid.ReplaceLineEndings("\r\n"),
            _ => valid
        };

        var checks = AuthorBriefDraftChecks.Run(markdown,
            CliAuthorDraftCommandTests.Fixture.MainSha, fixture.Repository);

        var check = Assert.Single(checks, check => check.Name == "premise-bullets");
        Assert.Equal(expected, check.Passed);
        Assert.Equal(BoardFillVerifierContract.BulletCount(BoardFillVerifierContract.Premise(markdown)) > 0,
            check.Passed);
        if (!expected)
            Assert.Contains("Restate each source claim as one bullet", check.Detail);
        if (shape == "prose")
            Assert.Equal(new[] { "premise-bullets" }, checks.Where(check => !check.Passed)
                .Select(check => check.Name).ToArray());
    }

    [Fact]
    public void Run_BulletlessFirstDraft_RevisionReceivesFailureAndReceiptRecordsIt()
    {
        using var fixture = new CliAuthorDraftCommandTests.Fixture();
        var valid = CliAuthorDraftCommandTests.ValidMarkdown;
        var invalid = Bulletless(valid);
        var failed = Assert.Single(AuthorBriefDraftChecks.Run(invalid,
            CliAuthorDraftCommandTests.Fixture.MainSha, fixture.Repository).Where(check => !check.Passed));
        Assert.Equal("premise-bullets", failed.Name);
        var requests = new List<WorkerProcessRunRequest>();
        var responses = new[] { Draft(invalid), Draft(valid) };

        var outcome = AuthorBriefDraftService.Run(fixture.Item.Id, fixture.Workspace, new((request, _) =>
        {
            requests.Add(request);
            return Task.FromResult(responses[Math.Min(requests.Count - 1, responses.Length - 1)]);
        }, fixture.Repository), fixture.Output, fixture.Error);

        Assert.Equal(0, outcome.ExitCode);
        Assert.Equal(2, requests.Count);
        Assert.Contains($"Failed premise-bullets: {failed.Detail}", requests[1].StandardInput!);
        Assert.Equal(valid, File.ReadAllText(outcome.DraftPath!));
        Assert.All(outcome.Checks, check => Assert.True(check.Passed, check.Detail));
        using var receipt = fixture.Receipt();
        Assert.Equal(2, receipt.RootElement.GetProperty("roundCount").GetInt32());
        var rounds = receipt.RootElement.GetProperty("rounds").EnumerateArray().ToArray();
        Assert.Equal(2, rounds.Length);
        Assert.True(rounds[0].GetProperty("checksRun").GetBoolean());
        Assert.Equal(new[] { "premise-bullets" }, rounds[0].GetProperty("failedChecks")
            .EnumerateArray().Select(check => check.GetString()).ToArray());
        Assert.Empty(rounds[1].GetProperty("failedChecks").EnumerateArray());
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void Provided_bullets_pass_and_count_without_head_citations(string newline)
    {
        using var fixture = new CliAuthorDraftCommandTests.Fixture();
        var markdown = CliAuthorDraftCommandTests.ValidMarkdown.Replace(
            "- Observed in `docs/role-capability-matrix.md:1`.",
            "  - provided-by-ABCdef: Supplies `missing/first.cs:1`.\n" +
            "    Continued claim at `missing/continued.cs:2`.\n" +
            "* provided-by-123456: Supplies the second part.").ReplaceLineEndings(newline);

        var checks = AuthorBriefDraftChecks.Run(markdown,
            CliAuthorDraftCommandTests.Fixture.MainSha, fixture.Repository,
            dependencyGoals: ["abcdef", "123456", "fedcba"]);

        Assert.NotEmpty(checks);
        Assert.All(checks, check => Assert.True(check.Passed, check.Detail));
        Assert.Equal("2 premise bullets.", Assert.Single(checks, check => check.Name == "premise-bullets").Detail);
        Assert.True(Assert.Single(checks, check => check.Name == "provided-by-goals").Passed);
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void Provided_bullet_does_not_exempt_an_ordinary_bullet(string newline)
    {
        using var fixture = new CliAuthorDraftCommandTests.Fixture();
        var markdown = CliAuthorDraftCommandTests.ValidMarkdown.Replace(
            "- Observed in `docs/role-capability-matrix.md:1`.",
            "- provided-by-abcdef: Supplies `missing/provided.cs:1`.\n" +
            "- Observed in `missing/ordinary.cs:1`.").ReplaceLineEndings(newline);

        var checks = AuthorBriefDraftChecks.Run(markdown,
            CliAuthorDraftCommandTests.Fixture.MainSha, fixture.Repository, dependencyGoals: ["abcdef"]);

        var failed = Assert.Single(checks.Where(check => !check.Passed));
        Assert.Equal("premise-citations", failed.Name);
        Assert.Contains("missing/ordinary.cs:1", failed.Detail);
        Assert.DoesNotContain("missing/provided.cs", failed.Detail);
        Assert.Equal("2 premise bullets.", Assert.Single(checks, check => check.Name == "premise-bullets").Detail);
    }

    [Fact]
    public void Skipped_citations_do_not_replace_the_preceding_continuation_path()
    {
        using var fixture = new CliAuthorDraftCommandTests.Fixture();
        var markdown = CliAuthorDraftCommandTests.ValidMarkdown.Replace(
            "- Observed in `docs/role-capability-matrix.md:1`.",
            "- Observed in `docs/role-capability-matrix.md:1`.\n" +
            "- provided-by-abcdef: Supplies `missing/provided.cs:1`.\n" +
            "- Continued at `:2`.");

        var checks = AuthorBriefDraftChecks.Run(markdown,
            CliAuthorDraftCommandTests.Fixture.MainSha, fixture.Repository, dependencyGoals: ["abcdef"]);

        Assert.NotEmpty(checks);
        Assert.All(checks, check => Assert.True(check.Passed, check.Detail));
    }

    private static string Bulletless(string markdown) => markdown.Replace("- Observed", "Observed");

    private static WorkerProcessRunResult Draft(string markdown) =>
        new(0, JsonSerializer.Serialize(new { kind = "draft", markdown }), "");
}
