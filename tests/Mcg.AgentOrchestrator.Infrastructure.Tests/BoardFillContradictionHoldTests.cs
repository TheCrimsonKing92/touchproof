using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core.Conductor;

// Parallel-safe: private fixture files/SQLite; note, git, model and clock seams are controlled.
public sealed class BoardFillContradictionHoldTests
{
    private const string Head = BoardFillAssessmentTestFixture.Head;
    private const string NextHead = "dddddddddddddddddddddddddddddddddddddddd";
    private static readonly DateTimeOffset Now = BoardFillReadyItemSelectorTests.Now;
    private static string Revised => BoardFillAssessmentTestFixture.Brief.Replace(
        "declares the feature.", "declares the revised feature.");

    [Fact]
    public void Format_parse_preserves_head_stamp_final_claims_and_evidence_paths()
    {
        const string premise = """
            ## Measured premise
            - Original claim.
            - Final contradicted claim.
              claims:
              evidence: src/Decoy.cs
            - Another contradicted claim.
            """;
        var text = BoardFillContradictionHold.Format(Head, Now, premise,
            [new(1, "verified", "src/Verified.cs:1"), new(2, "contradicted", "src/Feature/File.cs:17-19"),
             new(3, "contradicted", "src/Other.cs:4")]);
        Assert.Equal(BoardFillContradictionHold.Prefix, text.Split('\n')[0]);
        Assert.True(BoardFillContradictionHold.IsMarker(text));
        var marker = Assert.IsType<BoardFillContradictionHold>(BoardFillContradictionHold.Parse(text));
        Assert.Equal(Head, marker.MainHead);
        Assert.Equal(Now, marker.ItemChangeStamp);
        Assert.Equal(new[] { "src/Feature/File.cs", "src/Other.cs" }, marker.EvidencePaths);
        Assert.Equal(BoardFillVerdictRevision.BulletText(premise, 2) + "\n" +
            BoardFillVerdictRevision.BulletText(premise, 3), marker.Claims);
        Assert.DoesNotContain("Original claim", marker.Claims);
        Assert.Equal(marker.Claims, BoardFillContradictionHold.Parse(text.ReplaceLineEndings("\r\n"))!.Claims);
    }

    [Theory]
    [InlineData("plain note")]
    [InlineData(BoardFillContradictionHold.Prefix + " suffix\nmain-head: c")]
    [InlineData(BoardFillContradictionHold.Prefix + "\nmain-head: \nitem-change-stamp: invalid\nclaims:")]
    public void Malformed_text_does_not_parse(string text) => Assert.Null(BoardFillContradictionHold.Parse(text));

    [Theory]
    [InlineData("src/Feature/File.cs:17 explains the claim")]
    [InlineData("C:/Feature/File.cs:17")]
    [InlineData("../Feature/File.cs:17")]
    public void Non_citation_evidence_keeps_hold_without_comparing_paths(string evidence)
    {
        var text = BoardFillContradictionHold.Format(Head, Now, "- Claim.", [new(1, "contradicted", evidence)]);
        var marker = Assert.IsType<BoardFillContradictionHold>(BoardFillContradictionHold.Parse(text));
        Assert.Empty(marker.EvidencePaths);
        var calls = 0;
        Assert.False(marker.EvidenceChanged(NextHead, (_, _) => { calls++; return ["src/Feature/File.cs"]; }));
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task Revised_contradiction_writes_one_marker_after_assessment_and_still_fails()
    {
        using var h = new Harness("contradicted");
        await h.Harvest();

        var round = Assert.Single(h.Fixture.Store.ReadAll());
        Assert.True(round.RevisionAttempted);
        Assert.False(round.Assessment!.Fileable);
        Assert.Equal(new[] { "premise-bullet-1:contradicted" }, round.Assessment.FailingReasons);
        Assert.Equal(2, h.Verifications);
        Assert.Equal(1, h.Revisions);
        Assert.Equal(1, h.NoteAttempts);
        var note = Assert.Single(h.Fixture.Item.Notes);
        Assert.Equal(BoardFillContradictionHold.Prefix, note.Text.Split('\n')[0]);
        var marker = Assert.IsType<BoardFillContradictionHold>(BoardFillContradictionHold.Parse(note.Text));
        Assert.Equal(Head, marker.MainHead);
        Assert.Equal(Now, marker.ItemChangeStamp);
        Assert.Equal("- `src/Feature/File.cs:1` declares the revised feature.", marker.Claims);
        Assert.Equal(new[] { "src/Feature/File.cs" }, marker.EvidencePaths);
        Assert.Equal(round.ChangeStamp, BoardFillReadyItemSelector.ChangeStamp(h.Fixture.Item));
        h.Host.ServiceTick(h.Fixture.Kernel);
        Assert.Single(h.Fixture.Item.Notes);
    }

    [Theory]
    [InlineData("unverifiable", true, false, false)]
    [InlineData("verified", true, false, true)]
    [InlineData("verified", true, true, false)]
    [InlineData("contradicted", false, false, false)]
    public async Task Other_final_results_write_no_marker(string verdict, bool revise,
        bool preflightFailure, bool clean)
    {
        using var h = new Harness(verdict, revise: revise);
        // The clean second pass is triggered by a contradicted first pass; the preflight-only
        // case has verified bullets throughout and retains its preflight failure after revision.
        h.Verification = call => Verdict(clean && call == 1 ? "contradicted" : verdict);
        if (preflightFailure)
        {
            h.RevisedMarkdown += "\nRemove old tests.";
            File.WriteAllText(h.Fixture.DraftPath, h.RevisedMarkdown);
        }
        await h.Harvest();
        Assert.Empty(h.Fixture.Item.Notes);
        Assert.Equal(0, h.NoteAttempts);
        var round = Assert.Single(h.Fixture.Store.ReadAll());
        Assert.Equal(revise, round.RevisionAttempted);
        Assert.Equal(revise ? 2 : 1, h.Verifications);
        if (preflightFailure)
            Assert.Contains("preflight:missing-test-removal-bullet", round.Assessment!.FailingReasons);
        if (clean) Assert.True(round.Assessment!.Fileable);
    }

    [Fact]
    public async Task Marker_write_fault_leaves_assessed_round_and_harvest_usable()
    {
        using var h = new Harness("contradicted") { ThrowNote = true };
        await h.Harvest();
        Assert.Equal(1, h.NoteAttempts);
        Assert.NotNull(Assert.Single(h.Fixture.Store.ReadAll()).Assessment);
        Assert.Null(h.Host.CurrentRound);
        Assert.Empty(h.Fixture.Item.Notes);
        h.Host.ServiceTick(h.Fixture.Kernel);
        Assert.Equal(1, h.NoteAttempts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Held_tick_with_throwing_diff_or_unknown_head_never_starts(bool unknownHead)
    {
        using var h = new Harness("verified", maxDrafts: 2);
        h.AddMarker();
        h.CurrentHead = unknownHead ? null : NextHead;
        var calls = 0;
        h.ChangedFiles = (_, _) => { calls++; throw new InvalidOperationException("diff failed"); };
        h.Host.ServiceTick(h.Fixture.Kernel);
        Assert.Equal(unknownHead ? 0 : 1, calls);
        Assert.Null(h.Host.CurrentRound);
        Assert.Empty(h.Fixture.Store.ReadAll());
        Assert.Equal(0, h.NoteAttempts);
        Assert.Single(h.Fixture.Item.Notes);
    }

    [Fact]
    public void Release_note_write_fault_keeps_item_held_without_starting_round()
    {
        using var h = new Harness("verified", maxDrafts: 2) { ThrowNote = true, CurrentHead = NextHead };
        h.AddMarker();
        var calls = 0;
        h.ChangedFiles = (_, _) => { calls++; return ["src/Feature/File.cs"]; };
        h.Host.ServiceTick(h.Fixture.Kernel);
        Assert.Equal(1, calls);
        Assert.Equal(1, h.NoteAttempts);
        Assert.Null(h.Host.CurrentRound);
        Assert.Empty(h.Fixture.Store.ReadAll());
        Assert.Single(h.Fixture.Item.Notes);
    }

    [Fact]
    public async Task Changed_cited_file_records_one_release_and_allows_current_round()
    {
        using var h = new Harness("contradicted", maxDrafts: 2);
        h.Verification = call => Verdict(call <= 2 ? "contradicted" : "verified");
        await h.Harvest();
        Assert.Single(h.Fixture.Store.ReadAll());
        Assert.Single(h.Fixture.Item.Notes);
        h.CurrentHead = NextHead;
        h.Fixture.Clock.UtcNow += TimeSpan.FromMinutes(1);
        var diffs = new List<(string From, string To)>();
        h.ChangedFiles = (from, to) => { diffs.Add((from, to)); return ["src/Feature/File.cs"]; };
        h.Host.ServiceTick(h.Fixture.Kernel);
        Assert.NotNull(h.Host.CurrentRound);
        await h.CompleteRound();

        Assert.Equal(new[] { (Head, NextHead) }, diffs);
        Assert.Equal(2, h.NoteAttempts);
        Assert.Equal(2, h.Fixture.Item.Notes.Count);
        var release = h.Fixture.Item.Notes[1];
        Assert.False(BoardFillContradictionHold.IsMarker(release.Text));
        Assert.Contains(Head, release.Text);
        Assert.Contains(NextHead, release.Text);
        Assert.False(BoardFillReadyItemSelector.IsOwnerGated(h.Fixture.Item));
        var second = Assert.Single(h.Fixture.Store.ReadAll().Where(round => round.MainHead == NextHead));
        Assert.True(second.Assessment!.Fileable);
        Assert.DoesNotContain("backlog-changed", second.Assessment.FailingReasons);
        Assert.DoesNotContain("backlog-ineligible", second.Assessment.FailingReasons);
        h.Host.ServiceTick(h.Fixture.Kernel);
        Assert.Equal(2, h.NoteAttempts);
    }

    private static BoardFillPremiseVerification Verdict(string verdict) =>
        new("complete", 1, [new(1, verdict, "src/Feature/File.cs:17")]);

    private sealed class Harness : IDisposable
    {
        internal BoardFillAssessmentTestFixture Fixture { get; } = new();
        internal ConductorBoardFillHost Host { get; }
        internal int NoteAttempts { get; private set; }
        internal int Verifications { get; private set; }
        internal int Revisions { get; private set; }
        internal bool ThrowNote { get; set; }
        internal string? CurrentHead { get; set; } = Head;
        internal string RevisedMarkdown { get; set; } = Revised;
        internal Func<int, BoardFillPremiseVerification> Verification { get; set; }
        internal Func<string, string, IReadOnlyList<string>?> ChangedFiles { get; set; } = (_, _) => [];

        internal Harness(string finalVerdict, bool revise = true, int maxDrafts = 1)
        {
            Verification = _ => Verdict(finalVerdict);
            Host = new(Fixture.Store, (_, _) => Fixture.Draft() with { MainHead = CurrentHead },
                () => [Fixture.Item], (_, _) => BoardFillAssessmentTestFixture.Ready,
                () => ConductorAutonomyPolicy.Permissive with { BoardFillMaxDraftsPerDay = maxDrafts },
                new(Fixture.EventPath, utcNow: () => Fixture.Clock.UtcNow),
                items => items.Select(item => item.Id).ToHashSet(StringComparer.Ordinal), () => Fixture.Clock.UtcNow,
                new BoardFillAssessmentTestFixture.Verifier((_, _, _) =>
                    Task.FromResult(Verification(++Verifications))), mainHead: () => CurrentHead,
                revise: revise ? (request, _) =>
                {
                    Revisions++;
                    return Task.FromResult(new BoardFillVerdictRevisionResult("draft", 0, RevisedMarkdown,
                        request.MainHead, Fixture.Draft().Checks));
                } : null,
                appendBacklogNote: (id, text) =>
                {
                    NoteAttempts++;
                    Assert.Equal(Fixture.Item.Id, id);
                    Assert.All(Fixture.Store.ReadAll(), round => Assert.NotNull(round.Assessment));
                    if (ThrowNote) throw new InvalidOperationException("note write failed");
                    Fixture.Item = Fixture.Item with { UpdatedAt = Fixture.Clock.UtcNow,
                        Notes = [.. Fixture.Item.Notes, new(Fixture.Clock.UtcNow, text)] };
                }, changedFiles: (from, to) => ChangedFiles(from, to));
        }

        internal void AddMarker()
        {
            var note = BoardFillContradictionHold.Format(Head, Now,
                BoardFillVerifierContract.Premise(Revised), Verdict("contradicted").Verdicts);
            Fixture.Item = Fixture.Item with { UpdatedAt = Now.AddMinutes(1), Notes = [new(Now.AddMinutes(1), note)] };
        }

        internal async Task Harvest()
        {
            Host.ServiceTick(Fixture.Kernel);
            await CompleteRound();
        }

        internal async Task CompleteRound()
        {
            var round = Assert.IsAssignableFrom<Task>(Host.CurrentRound);
            try { await round.WaitAsync(TimeSpan.FromSeconds(30)); }
            catch (TimeoutException) { throw new Xunit.Sdk.XunitException("Board-fill draft round did not complete"); }
            Fixture.Clock.UtcNow += TimeSpan.FromMinutes(1);
            Host.ServiceTick(Fixture.Kernel);
        }

        public void Dispose()
        {
            Host.Stop();
            Fixture.Dispose();
        }
    }
}
