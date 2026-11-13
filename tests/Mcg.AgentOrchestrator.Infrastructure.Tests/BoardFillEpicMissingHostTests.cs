using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each test owns all files, stores and model seams.
public sealed class BoardFillEpicMissingHostTests
{
    [Fact]
    public async Task Missing_epic_announces_once_and_assignment_starts_next_tick()
    {
        using var h = new BoardFillTestHarness();
        h.Policy = h.Policy with { BoardFillMode = ConductorBoardFillMode.Shadow };
        var members = new HashSet<string>();
        h.EpicMembership = _ => members;
        var item = Assert.Single(h.Items);
        for (var tick = 0; tick < 3; tick++) h.Host.ServiceTick(h.Kernel);
        Assert.Null(h.Host.CurrentRound);
        Assert.Empty(h.Store.ReadAll());
        Assert.Equal(0, h.Calls);
        var question = Assert.Single(Details(h.EventsPath, "BOARD_FILL_EPIC_MISSING"));
        Assert.StartsWith($"BOARD_FILL_EPIC_MISSING backlog={item.Id[..8]}", question);
        Assert.Contains(item.Title, question);
        Assert.Contains("Which epic", question);

        members.Add(item.Id);
        h.Host.ServiceTick(h.Kernel);
        Assert.Equal(item.Id, Assert.Single(h.Store.ReadAll()).BacklogItemId);
        await PanelTestHarness.Signal(h.Host.CurrentRound!, "draft after epic assignment finished");
        Assert.Equal(1, h.Calls);
    }

    [Fact]
    public async Task Default_wiring_reads_assignment_and_rearms_owner_question()
    {
        using var f = new CliAuthorDraftCommandTests.Fixture();
        var policy = ConductorAutonomyPolicy.Permissive with
            { BoardFillMode = ConductorBoardFillMode.Shadow, BoardFillMaxDraftsPerDay = 10 };
        var calls = 0;
        var host = ConductorBoardFillHost.CreateDefault(f.Workspace, () => policy,
            () => CliAuthorDraftCommandTests.Fixture.MainSha, (_, _) =>
            {
                Interlocked.Increment(ref calls);
                return new("failed", 1, null, null, null, [], "stub failure");
            });
        try
        {
            host.ServiceTick(new());
            host.ServiceTick(new());
            Assert.Null(host.CurrentRound);
            Assert.Equal(0, calls);
            Assert.Single(Details(f.Workspace.ConductEventsLogPath, "BOARD_FILL_EPIC_MISSING"));
            var attention = CollaborationItemStore.ForDirectory(f.Workspace.OrchestratorDirectory);
            var question = Assert.Single(await attention.ListAsync());
            Assert.Equal(CollaborationItemType.Decision, question.Type);
            Assert.Contains(f.Item.Title, question.Body);

            var portfolio = new PortfolioStore(f.Workspace.PortfolioStorePath);
            var epic = await portfolio.AddEpicAsync("Assigned work");
            await portfolio.AssignBacklogItemToEpicAsync(f.Item.Id, epic.Id);
            var before = File.ReadAllBytes(f.Workspace.PortfolioStorePath);
            host.ServiceTick(new());
            await PanelTestHarness.Signal(host.CurrentRound!, "default membership draft finished");
            Assert.Equal(1, calls);
            Assert.Equal(before, File.ReadAllBytes(f.Workspace.PortfolioStorePath));
            Assert.True(CollaborationItemLifecycle.IsTerminal(Assert.Single(await attention.ListAsync()).Status));

            using (var connection = new Microsoft.Data.Sqlite.SqliteConnection(
                "Data Source=" + f.Workspace.PortfolioStorePath + ";Pooling=False"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "DELETE FROM backlog_epic_memberships WHERE backlog_item_id=$id";
                command.Parameters.AddWithValue("$id", f.Item.Id);
                Assert.Equal(1, command.ExecuteNonQuery());
            }
            host.ServiceTick(new());
            Assert.Null(host.CurrentRound);
            Assert.Equal(2, Details(f.Workspace.ConductEventsLogPath, "BOARD_FILL_EPIC_MISSING").Length);
            Assert.Single((await attention.ListAsync()).Where(item => !CollaborationItemLifecycle.IsTerminal(item.Status)));
        }
        finally { host.Stop(); }
    }

    [Fact]
    public void Read_fault_blocks_drafting_and_filing_for_the_tick()
    {
        using var f = new BoardFillFilingTestFixture();
        var draft = f.Assessed();
        var calls = 0;
        var reads = 0;
        var host = new ConductorBoardFillHost(f.Drafts.Store, (_, _) =>
            { calls++; return f.Drafts.Draft(); },
            () => [f.Drafts.Item, BoardFillReadyItemSelectorTests.Item('b')],
            (_, _) => BoardFillAssessmentTestFixture.Ready,
            () => f.Policy with { BoardFillMaxDraftsPerDay = 10 }, new(f.Drafts.EventPath),
            _ => { reads++; throw new IOException("membership unreadable"); },
            filing: new(f.Intake, () => f.Board, () => f.Head, (_, _) => []));
        try
        {
            for (var tick = 0; tick < 3; tick++) host.ServiceTick(f.Drafts.Kernel);
            Assert.Equal(3, reads);
            Assert.Equal(0, calls);
            Assert.Null(host.CurrentRound);
            Assert.Null(host.CurrentFiling);
            Assert.Empty(f.Intake.Requests);
            Assert.Equal(draft.Id, Assert.Single(f.Drafts.Store.ReadAll()).Id);
            Assert.Empty(f.Drafts.Store.ReadAll().Single().Filings ?? []);
            Assert.Single(Details(f.Drafts.EventPath, "BOARD_FILL_EPIC_READ_FAILED"));
            Assert.Empty(Details(f.Drafts.EventPath, "BOARD_FILL_EPIC_MISSING"));
        }
        finally { host.Stop(); }
    }

    [Fact]
    public void Default_wiring_records_corrupt_membership_store_without_drafting()
    {
        using var f = new CliAuthorDraftCommandTests.Fixture();
        File.WriteAllText(f.Workspace.PortfolioStorePath, "not SQLite");
        var calls = 0;
        var host = ConductorBoardFillHost.CreateDefault(f.Workspace,
            () => ConductorAutonomyPolicy.Permissive with { BoardFillMode = ConductorBoardFillMode.File },
            draft: (_, _) => { calls++; throw new InvalidOperationException("No draft expected"); });
        try
        {
            host.ServiceTick(new());
            Assert.Null(host.CurrentRound);
            Assert.Null(host.CurrentFiling);
            Assert.Equal(0, calls);
            Assert.Single(Details(f.Workspace.ConductEventsLogPath, "BOARD_FILL_EPIC_READ_FAILED"));
            Assert.Empty(Details(f.Workspace.ConductEventsLogPath, "BOARD_FILL_EPIC_MISSING"));
        }
        finally { host.Stop(); }
    }

    [Fact]
    public async Task Admission_read_fault_prevents_another_draft_in_the_same_tick()
    {
        using var f = new BoardFillFilingTestFixture();
        var draft = f.Assessed();
        var items = new[] { f.Drafts.Item, BoardFillReadyItemSelectorTests.Item('b') };
        var reads = 0;
        var host = new ConductorBoardFillHost(f.Drafts.Store,
            (_, _) => throw new InvalidOperationException("No draft expected while admission is pending"),
            () => items, (_, _) => BoardFillAssessmentTestFixture.Ready,
            () => f.Policy with { BoardFillMaxDraftsPerDay = 10 }, new(f.Drafts.EventPath),
            candidates => Interlocked.Increment(ref reads) == 1
                ? candidates.Select(item => item.Id).ToHashSet(StringComparer.Ordinal)
                : throw new IOException("admission membership unreadable"),
            filing: new(f.Intake, () => f.Board, () => f.Head, (_, _) => []));
        try
        {
            host.ServiceTick(f.Drafts.Kernel);
            await PanelTestHarness.Signal(host.CurrentFiling!, "admission read fault recorded");
            Assert.Equal(2, reads);
            Assert.Null(host.CurrentRound);
            Assert.Equal(draft.Id, Assert.Single(f.Drafts.Store.ReadAll()).Id);
            Assert.Empty(f.Intake.Requests);
            Assert.Equal("epic-membership-unavailable", Assert.Single(f.Drafts.Store.ReadAll().Single().Filings!).Reason);
            Assert.Single(Details(f.Drafts.EventPath, "BOARD_FILL_EPIC_READ_FAILED"));
        }
        finally { host.Stop(); }
    }

    [Fact]
    public async Task Batch_read_preserves_membership_for_closed_recovery_candidates()
    {
        using var f = new CliAuthorDraftCommandTests.Fixture();
        var open = await f.Store.AddAsync("Open candidate");
        var missing = await f.Store.AddAsync("Unassigned candidate");
        await f.Store.CloseAsync(f.Item.Id);
        var portfolio = new PortfolioStore(f.Workspace.PortfolioStorePath);
        var epic = await portfolio.AddEpicAsync("Recovery work");
        await portfolio.AssignBacklogItemToEpicAsync(f.Item.Id, epic.Id);
        await portfolio.AssignBacklogItemToEpicAsync(open.Id, epic.Id);
        var before = File.ReadAllBytes(f.Workspace.PortfolioStorePath);

        var members = BoardFillEpicMissingQuestion.ReadMemberships(f.Workspace.PortfolioStorePath,
            BoardFillBacklogSnapshot.Read(f.Workspace.BacklogStorePath));
        Assert.Equal(2, members.Count);
        Assert.Contains(f.Item.Id, members);
        Assert.Contains(open.Id, members);
        Assert.DoesNotContain(missing.Id, members);
        Assert.Equal(before, File.ReadAllBytes(f.Workspace.PortfolioStorePath));
    }

    internal static string[] Details(string path, string prefix) => !File.Exists(path) ? [] :
        File.ReadAllLines(path).Select(line =>
        {
            using var json = JsonDocument.Parse(line);
            return json.RootElement.GetProperty("detail").GetString()!;
        }).Where(detail => detail.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
}
