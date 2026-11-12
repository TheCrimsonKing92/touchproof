using Mcg.AgentOrchestrator.App.Orchestration;

// Parallel-safe: each fixture owns a unique SQLite/event root and a fixed clock.
public sealed class ConductorBoardFillHostDraftReasonsTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Finished_round_uses_stored_reasons_despite_opposite_verification(bool fileable)
    {
        using var h = new BoardFillAssessmentTestFixture();
        var verification = fileable
            ? new BoardFillPremiseVerification("complete", 1, [new(1, "contradicted", "src/Feature/File.cs:1")])
            : BoardFillAssessmentTestFixture.Verified;
        string[] reasons = fileable ? [] :
            ["premise-bullet-4:unverifiable", "verification:unavailable no-premise-bullets"];

        var detail = ReportStored(h, fileable, reasons, verification);

        var expectedReasons = fileable ? "none" :
            "premise-bullet-4:unverifiable; verification:unavailable no-premise-bullets";
        Assert.Equal(Prefix(h, fileable, fileable ? 0 : 1) + expectedReasons, detail);
    }

    [Fact]
    public void Reasons_with_line_breaks_emit_one_space_and_preserve_other_whitespace()
    {
        using var h = new BoardFillAssessmentTestFixture();

        var detail = ReportStored(h, false, ["a\r\nb\rc\nd\u0085e\u2028f\u2029g\t\f h"]);

        Assert.Equal(Prefix(h, false) + "a b c d e f g\t\f h", detail);
        Assert.DoesNotContain(detail, character => character is '\r' or '\n' or '\u0085' or '\u2028' or '\u2029');
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void Joined_payload_at_400_and_401_units_keeps_label_and_bounds_reasons(int tailLength)
    {
        using var h = new BoardFillAssessmentTestFixture();
        var first = new string('a', 398);

        var detail = ReportStored(h, false, [first, new string('b', tailLength)]);

        Assert.Equal(Prefix(h, false) + first + "; " + (tailLength == 0 ? "" : "..."), detail);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Empty_reason_list_emits_none_independently_of_fileable_flag(bool fileable)
    {
        using var h = new BoardFillAssessmentTestFixture();

        var detail = ReportStored(h, fileable, []);

        Assert.Equal(Prefix(h, fileable) + "none", detail);
    }

    [Fact]
    public void Normalized_payload_at_400_units_is_not_truncated()
    {
        using var h = new BoardFillAssessmentTestFixture();

        var detail = ReportStored(h, false, [new string('a', 399) + "\r\n"]);

        Assert.Equal(Prefix(h, false) + new string('a', 399) + " ", detail);
    }

    [Theory]
    [InlineData(new[] { "" }, "")]
    [InlineData(new[] { "second", "", "first", "second" }, "second; ; first; second")]
    public void Nonempty_reason_list_preserves_entries_order_and_duplicates(string[] reasons, string expected)
    {
        using var h = new BoardFillAssessmentTestFixture();

        var detail = ReportStored(h, false, reasons);

        Assert.Equal(Prefix(h, false) + expected, detail);
    }

    private static string Prefix(BoardFillAssessmentTestFixture h, bool fileable, int verified = 1) =>
        $"BOARD_FILL_DRAFT backlog={h.Item.Id[..8]} outcome=draft checks=4/4 draft={h.DraftPath}" +
        $" verified={verified}/1 fileable={fileable.ToString().ToLowerInvariant()} depends=none reasons=";

    private static string ReportStored(BoardFillAssessmentTestFixture h, bool fileable, string[] reasons,
        BoardFillPremiseVerification? verification = null)
    {
        var round = h.Finished();
        h.Store.Assess(round.Id, new(verification ?? BoardFillAssessmentTestFixture.Verified,
            round.Checks, BoardFillAssessmentTestFixture.NoDepends, fileable, reasons));
        var verifierCalls = 0;
        var host = h.Host(new BoardFillAssessmentTestFixture.Verifier((_, _, _) =>
        {
            verifierCalls++;
            throw new InvalidOperationException("An assessed round must not invoke the verifier.");
        }));

        host.ServiceTick(h.Kernel);

        Assert.Equal(0, verifierCalls);
        Assert.Null(host.CurrentRound);
        Assert.True(Assert.Single(h.Store.ReadAll()).Reported);
        using var entry = BoardFillTestHarness.Event(Assert.Single(File.ReadAllLines(h.EventPath)));
        Assert.Equal("board-fill-draft", entry.RootElement.GetProperty("eventKind").GetString());
        return entry.RootElement.GetProperty("detail").GetString()!;
    }
}
