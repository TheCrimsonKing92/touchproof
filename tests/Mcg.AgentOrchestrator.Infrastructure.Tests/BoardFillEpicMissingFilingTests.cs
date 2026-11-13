using Mcg.AgentOrchestrator.App.Orchestration;

// Parallel-safe: private SQLite history and fake intake, with an injected clock.
public sealed class BoardFillEpicMissingFilingTests
{
    [Fact]
    public async Task Admission_refuses_removed_epic_and_restoration_files_on_next_tick()
    {
        using var f = new BoardFillFilingTestFixture();
        var draft = f.Assessed();
        var members = new HashSet<string> { f.Drafts.Item.Id };
        f.EpicMembership = _ => members;
        // Remove membership after the other admission reads, before the mutation boundary.
        f.BeforeHead = () => members.Clear();
        var attempt = f.Host.AttemptFiling(draft.Id);
        Assert.Equal("refused", attempt.Result);
        Assert.Equal("not-fileable:epic-missing", attempt.Reason);
        Assert.NotNull(attempt.FinishedAt);
        Assert.False(attempt.Terminal);
        Assert.Empty(f.Intake.Requests);
        Assert.Contains(f.Drafts.Item.Title,
            Assert.Single(BoardFillEpicMissingHostTests.Details(f.Drafts.EventPath, "BOARD_FILL_EPIC_MISSING")));
        for (var tick = 0; tick < 3; tick++) f.Host.ServiceTick(f.Drafts.Kernel);
        Assert.Null(f.Host.CurrentFiling);
        Assert.Single(f.Drafts.Store.ReadAll().Single().Filings!);

        f.BeforeHead = null;
        members.Add(f.Drafts.Item.Id);
        f.Host.ServiceTick(f.Drafts.Kernel);
        await PanelTestHarness.Signal(f.Host.CurrentFiling!, "filing after epic restoration finished");
        Assert.Single(f.Intake.Requests);
        Assert.Equal("filed", f.Drafts.Store.ReadAll().Single().Filings!.Last().Result);
    }

    [Fact]
    public void Current_epic_admits_the_same_draft()
    {
        using var f = new BoardFillFilingTestFixture();
        var draft = f.Assessed();
        f.EpicMembership = _ => new HashSet<string> { f.Drafts.Item.Id };
        Assert.Equal("filed", f.Host.AttemptFiling(draft.Id).Result);
        Assert.Single(f.Intake.Requests);
    }

    [Fact]
    public async Task Filing_hold_keeps_draft_without_attempt_churn_until_assignment()
    {
        using var f = new BoardFillFilingTestFixture();
        var draft = f.Assessed();
        var members = new HashSet<string>();
        f.EpicMembership = _ => members;
        for (var tick = 0; tick < 3; tick++) f.Host.ServiceTick(f.Drafts.Kernel);
        Assert.Null(f.Host.CurrentFiling);
        Assert.Empty(f.Intake.Requests);
        Assert.Empty(f.Drafts.Store.ReadAll().Single().Filings ?? []);
        Assert.Single(BoardFillEpicMissingHostTests.Details(f.Drafts.EventPath, "BOARD_FILL_EPIC_MISSING"));

        members.Add(f.Drafts.Item.Id);
        f.Host.ServiceTick(f.Drafts.Kernel);
        await PanelTestHarness.Signal(f.Host.CurrentFiling!, "held draft filed after assignment");
        Assert.Equal(draft.Id, Assert.Single(f.Intake.Requests).DraftId);
        Assert.Equal("filed", Assert.Single(f.Drafts.Store.ReadAll().Single().Filings!).Result);
    }

    [Fact]
    public void Admission_read_fault_is_unknown_and_never_calls_intake()
    {
        using var f = new BoardFillFilingTestFixture();
        var draft = f.Assessed();
        f.EpicMembership = _ => throw new IOException("membership unreadable");
        var attempt = f.Host.AttemptFiling(draft.Id);
        Assert.Equal("refused", attempt.Result);
        Assert.Equal("epic-membership-unavailable", attempt.Reason);
        Assert.False(attempt.Terminal);
        Assert.Empty(f.Intake.Requests);
        Assert.Single(BoardFillEpicMissingHostTests.Details(f.Drafts.EventPath, "BOARD_FILL_EPIC_READ_FAILED"));
        Assert.Empty(BoardFillEpicMissingHostTests.Details(f.Drafts.EventPath, "BOARD_FILL_EPIC_MISSING"));
    }
}
