using Mcg.AgentOrchestrator.App.Orchestration;

// Parallel-safe: every fixture owns its SQLite store and files; model seams never write files.
public sealed class ConductorBoardFillHostVerdictRevisionTests
{
    private const string Evidence = "src/Feature/File.cs:17 contradicts the original claim.";
    private const string PreflightReason = "preflight:missing-test-removal-bullet";
    private static string Revised => BoardFillAssessmentTestFixture.Brief.Replace(
        "`src/Feature/File.cs:1` declares the feature.", "`src/Feature/File.cs:17` declares the corrected feature.");

    [Fact]
    public async Task Contradicted_then_verified_ReplacesDraftAndPersistsBothPasses()
    {
        using var h = new BoardFillAssessmentTestFixture();
        var requests = new List<BoardFillVerdictRevisionRequest>();
        var verifiedInputs = new List<(string Premise, string Head)>();
        var host = h.Host(new BoardFillAssessmentTestFixture.Verifier((premise, head, _) =>
        {
            verifiedInputs.Add((premise, head));
            return Task.FromResult(verifiedInputs.Count == 1 ? Verdict("contradicted") :
                BoardFillAssessmentTestFixture.Verified);
        }), (request, _) =>
        {
            requests.Add(request);
            return Task.FromResult(Revision(h));
        });

        await Harvest(h, host);

        var request = Assert.Single(requests);
        Assert.Equal(h.Item.Id, request.BacklogItemId);
        Assert.Equal(BoardFillAssessmentTestFixture.Head, request.MainHead);
        Assert.Contains("- `src/Feature/File.cs:1` declares the feature.", request.Prompt);
        Assert.Contains("Verdict: contradicted", request.Prompt);
        Assert.Contains("Evidence: " + Evidence, request.Prompt);
        Assert.Contains(BoardFillAssessmentTestFixture.Brief, request.Prompt);
        Assert.Equal(new[] { BoardFillVerifierContract.Premise(BoardFillAssessmentTestFixture.Brief),
            BoardFillVerifierContract.Premise(Revised) }, verifiedInputs.Select(input => input.Premise));
        Assert.All(verifiedInputs, input => Assert.Equal(BoardFillAssessmentTestFixture.Head, input.Head));
        var round = Assert.Single(h.Store.ReadAll());
        Assert.True(round.Assessment!.Fileable);
        Assert.Empty(round.Assessment.FailingReasons);
        Assert.True(round.RevisionAttempted);
        Assert.Equal(new[] { "premise-bullet-1:contradicted" }, round.FirstPassFailingReasons);
        Assert.Equal(h.DraftPath, round.DraftPath);
        Assert.Equal(Revised, File.ReadAllText(h.DraftPath));
        Assert.Equal("revised", Assert.Single(round.Checks.Where(check => check.Name == "sections")).Detail);
    }

    [Theory]
    [InlineData("contradicted", "unverifiable")]
    [InlineData("unverifiable", "contradicted")]
    public async Task Still_nonverified_AssessesOnlySecondPassWithoutAnotherRevision(
        string firstVerdict, string secondVerdict)
    {
        using var h = new BoardFillAssessmentTestFixture();
        var verifierCalls = 0;
        var revisionCalls = 0;
        var host = h.Host(new BoardFillAssessmentTestFixture.Verifier((_, _, _) =>
            Task.FromResult(Verdict(++verifierCalls == 1 ? firstVerdict : secondVerdict))), (_, _) =>
        {
            revisionCalls++;
            return Task.FromResult(Revision(h));
        });

        await Harvest(h, host);

        Assert.Equal(1, revisionCalls);
        Assert.Equal(2, verifierCalls);
        var round = Assert.Single(h.Store.ReadAll());
        Assert.False(round.Assessment!.Fileable);
        Assert.Equal(new[] { "premise-bullet-1:" + secondVerdict }, round.Assessment.FailingReasons);
        Assert.Equal(new[] { "premise-bullet-1:" + firstVerdict }, round.FirstPassFailingReasons);
        Assert.True(round.RevisionAttempted);
        Assert.Equal(Revised, File.ReadAllText(h.DraftPath));
    }

    [Fact]
    public async Task Preflight_failure_QuotesDetailAndReassessesRevisedDraft()
    {
        using var h = new BoardFillAssessmentTestFixture();
        var original = BoardFillAssessmentTestFixture.Brief + "\nRemove old tests.";
        File.WriteAllText(h.DraftPath, original);
        var failedCheck = Assert.Single(BoardFillPreflightChecks.Run(original).Where(check => !check.Passed));
        Assert.Equal(PreflightReason, failedCheck.Name);
        Assert.NotEmpty(failedCheck.Detail);
        var requests = new List<BoardFillVerdictRevisionRequest>();
        var verifierCalls = 0;
        var host = h.Host(new BoardFillAssessmentTestFixture.Verifier((_, _, _) =>
        {
            verifierCalls++;
            return Task.FromResult(BoardFillAssessmentTestFixture.Verified);
        }), (request, _) =>
        {
            requests.Add(request);
            return Task.FromResult(Revision(h));
        });

        await Harvest(h, host);

        var prompt = Assert.Single(requests).Prompt;
        Assert.Contains(failedCheck.Name, prompt);
        Assert.Contains(failedCheck.Detail, prompt);
        Assert.Contains(original, prompt);
        Assert.Equal(2, verifierCalls);
        var round = Assert.Single(h.Store.ReadAll());
        Assert.True(round.Assessment!.Fileable);
        Assert.All(round.Assessment.PreflightChecks, check => Assert.True(check.Passed));
        Assert.Equal(new[] { PreflightReason }, round.FirstPassFailingReasons);
        Assert.True(round.RevisionAttempted);
        Assert.Equal(Revised, File.ReadAllText(h.DraftPath));
    }

    [Theory]
    [InlineData("complete")]
    [InlineData("failed")]
    [InlineData("unavailable")]
    [InlineData("invalid")]
    public async Task Clean_or_faultedVerification_DoesNotInvokeRevision(string status)
    {
        using var h = new BoardFillAssessmentTestFixture();
        if (status != "complete")
            File.AppendAllText(h.DraftPath, "\nRemove old tests.");
        var original = File.ReadAllBytes(h.DraftPath);
        var verifierCalls = 0;
        var revisionCalls = 0;
        var host = h.Host(new BoardFillAssessmentTestFixture.Verifier((_, _, _) =>
        {
            verifierCalls++;
            return Task.FromResult(status == "complete" ? BoardFillAssessmentTestFixture.Verified :
                new BoardFillPremiseVerification(status, 1, [], "fault"));
        }), (_, _) =>
        {
            revisionCalls++;
            return Task.FromResult(Revision(h));
        });

        await Harvest(h, host);

        Assert.Equal(0, revisionCalls);
        Assert.Equal(1, verifierCalls);
        Assert.Equal(original, File.ReadAllBytes(h.DraftPath));
        var round = Assert.Single(h.Store.ReadAll());
        Assert.False(round.RevisionAttempted);
        Assert.Equal(status == "complete", round.Assessment!.Fileable);
        Assert.Equal(round.Assessment.FailingReasons, round.FirstPassFailingReasons);
        if (status == "complete") Assert.Empty(round.FirstPassFailingReasons!);
        else
        {
            Assert.Contains("verification:" + status + " fault", round.Assessment.FailingReasons);
            Assert.Contains(PreflightReason, round.Assessment.FailingReasons);
        }
    }

    [Theory]
    [InlineData("stale")]
    [InlineData("unparseable")]
    [InlineData("nonzero")]
    [InlineData("head-changed")]
    [InlineData("throws")]
    [InlineData("failed")]
    [InlineData("blank")]
    public async Task Rejected_revision_PreservesOriginalBytesAndFirstPassReasons(string rejection)
    {
        using var h = new BoardFillAssessmentTestFixture();
        File.AppendAllText(h.DraftPath, "\nRemove old tests.");
        var original = File.ReadAllBytes(h.DraftPath);
        var verifierCalls = 0;
        var revisionCalls = 0;
        var host = h.Host(new BoardFillAssessmentTestFixture.Verifier((_, _, _) =>
        {
            verifierCalls++;
            return Task.FromResult(Verdict("contradicted"));
        }), (_, _) =>
        {
            revisionCalls++;
            if (rejection == "throws") throw new InvalidOperationException("Author failed");
            var result = Revision(h);
            return Task.FromResult(rejection switch
            {
                "nonzero" => result with { ExitCode = 1 },
                "head-changed" => result with { MainHead = new string('d', 40) },
                "blank" => result with { Markdown = " \r\n" },
                _ => result with { Kind = rejection }
            });
        });

        await Harvest(h, host);

        Assert.Equal(1, revisionCalls);
        Assert.Equal(1, verifierCalls);
        Assert.Equal(original, File.ReadAllBytes(h.DraftPath));
        var round = Assert.Single(h.Store.ReadAll());
        Assert.True(round.RevisionAttempted);
        Assert.False(round.Assessment!.Fileable);
        Assert.Equal(new[] { "premise-bullet-1:contradicted", PreflightReason }, round.Assessment.FailingReasons);
        Assert.Equal(round.Assessment.FailingReasons, round.FirstPassFailingReasons);
        Assert.All(round.Checks, check => Assert.Equal("ok", check.Detail));
    }

    private static BoardFillPremiseVerification Verdict(string verdict) =>
        new("complete", 1, [new(1, verdict, Evidence)]);

    private static BoardFillVerdictRevisionResult Revision(BoardFillAssessmentTestFixture h) =>
        new("draft", 0, Revised, BoardFillAssessmentTestFixture.Head,
            h.Draft().Checks.Select(check => check with { Detail = "revised" }).ToArray());

    private static async Task Harvest(BoardFillAssessmentTestFixture h, ConductorBoardFillHost host)
    {
        host.ServiceTick(h.Kernel);
        await PanelTestHarness.Signal(host.CurrentRound!, "board-fill verdict revision round finished");
        host.ServiceTick(h.Kernel);
    }
}
