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

    private static string Bulletless(string markdown) => markdown.Replace("- Observed", "Observed");

    private static WorkerProcessRunResult Draft(string markdown) =>
        new(0, JsonSerializer.Serialize(new { kind = "draft", markdown }), "");
}
