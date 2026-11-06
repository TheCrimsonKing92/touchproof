using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

// Parallel-safe: fake model, with git, SQLite and receipts confined to unique fixture roots.
public sealed class AuthorStaleRetirementTests
{
    [Fact]
    public async Task Stale_result_appends_reason_and_ordered_evidence_without_changing_output()
    {
        using var fixture = new CliAuthorDraftCommandTests.Fixture();
        await fixture.Store.UpdateAsync(fixture.Item.Id, new(Tags: "existing-tag"));
        var before = (await fixture.Store.GetByExactIdAsync(fixture.Item.Id))!;
        const string reason = "The requested capability already exists.";
        string[] evidence = ["docs/state.md:3", "docs/second.md:7", "docs/state.md:3"];

        var code = fixture.Run(JsonSerializer.Serialize(new { kind = "stale", reason, evidenceReferences = evidence }));

        Assert.Equal(2, code);
        Assert.Equal(reason + fixture.Output.NewLine, fixture.Output.ToString());
        Assert.Empty(fixture.Error.ToString());
        var current = (await fixture.Store.GetByExactIdAsync(fixture.Item.Id))!;
        Assert.Equal(before.Notes.Count + 1, current.Notes.Count);
        Assert.All(before.Notes, note => Assert.Contains(note, current.Notes));
        var appended = Assert.Single(current.Notes.Except(before.Notes));
        Assert.Equal(reason + Environment.NewLine + "Evidence:" + Environment.NewLine +
            string.Join(Environment.NewLine, evidence.Select(reference => "- " + reference)), appended.Text);
        Assert.Equal(BacklogItemStatus.Open, current.Status);
        Assert.Equal("existing-tag,author-refused", current.Tags);
        Assert.Empty(Directory.GetFiles(fixture.Drafts, "*.md"));
        using var receipt = fixture.Receipt();
        Assert.Equal("stale", receipt.RootElement.GetProperty("kind").GetString());
        Assert.Equal(reason, receipt.RootElement.GetProperty("staleReason").GetString());
        Assert.Equal(evidence, receipt.RootElement.GetProperty("evidenceReferences").EnumerateArray()
            .Select(reference => reference.GetString()).ToArray());
        Assert.Equal(JsonValueKind.Null, receipt.RootElement.GetProperty("failure").ValueKind);
    }

    [Theory]
    [InlineData("ancestor-reason", true)]
    [InlineData("head-evidence", true)]
    [InlineData("side-branch", false)]
    [InlineData("no-commit", false)]
    [InlineData("missing-commit", false)]
    [InlineData("overlong-prefix", false)]
    [InlineData("overlong-suffix", false)]
    [InlineData("short-hex", false)]
    public async Task Commit_reference_closes_only_when_it_resolves_on_main(string reference, bool closes)
    {
        using var repo = new GitTrackedEditsProbeTests.RepositoryFixture();
        var ancestor = repo.Git("rev-parse", "HEAD");
        repo.Git("commit", "-q", "--allow-empty", "-m", "Next main");
        var head = repo.Git("rev-parse", "HEAD");
        repo.Git("checkout", "-q", "-b", "side", ancestor);
        repo.Git("commit", "-q", "--allow-empty", "-m", "Side only");
        var side = repo.Git("rev-parse", "HEAD");
        repo.Git("checkout", "-q", "main");
        Assert.NotEqual(ancestor, head);
        Assert.Equal(0, GitCli.Run(repo.Root, "merge-base", "--is-ancestor", ancestor, head).ExitCode);
        var nonAncestor = GitCli.Run(repo.Root, "merge-base", "--is-ancestor", side, head);
        Assert.True(nonAncestor.ProcessStarted && !nonAncestor.DrainTimedOut);
        Assert.Equal(1, nonAncestor.ExitCode);

        var workspace = OrchestratorWorkspace.ForDirectory(repo.Root);
        var store = new BacklogStore(workspace.BacklogStorePath);
        var item = await store.AddAsync("Ready item", "Build the receipt");
        await store.UpdateAsync(item.Id, new(Tags: closes ? "keep,AUTHOR-REFUSED" : "keep"));
        var reason = reference switch
        {
            "ancestor-reason" => $"Already shipped in {ancestor[..7]}",
            "side-branch" => $"Reference {side}",
            "missing-commit" => "Reference 0000000000000000000000000000000000000000",
            "overlong-prefix" => $"Reference f{head}",
            "overlong-suffix" => $"Reference {head}f",
            "short-hex" => $"Reference {head[..6]}",
            _ => "Already implemented"
        };
        string[] evidence = reference == "head-evidence"
            ? ["0000000000000000000000000000000000000000", $"https://example.test/commit/{head}"]
            : ["seed.txt:1"];
        var calls = 0;
        using var output = new StringWriter();
        using var error = new StringWriter();
        var outcome = AuthorBriefDraftService.Run(item.Id, workspace, new((_, _) =>
        {
            calls++;
            return Task.FromResult(new WorkerProcessRunResult(0,
                JsonSerializer.Serialize(new { kind = "stale", reason, evidenceReferences = evidence }), ""));
        }, new GitAuthorBriefDraftRepository(repo.Root, "main")), output, error);

        Assert.Equal(1, calls);
        Assert.Equal("stale", outcome.Kind);
        Assert.Equal(2, outcome.ExitCode);
        Assert.Equal(head, outcome.MainHead);
        Assert.Equal(reason + output.NewLine, output.ToString());
        Assert.Empty(error.ToString());
        var current = (await store.GetByExactIdAsync(item.Id))!;
        Assert.Equal(closes ? BacklogItemStatus.Done : BacklogItemStatus.Open, current.Status);
        Assert.Equal(closes ? "keep" : "keep,author-refused", current.Tags);
        Assert.Contains(reason, Assert.Single(current.Notes).Text);
        if (closes)
        {
            Assert.Contains("Closed: " + reason, current.Body);
            var reopened = await store.ReopenAsync(item.Id);
            Assert.Same(reopened, BoardFillReadyItemSelector.Select([reopened], [], new HashSet<string>(),
                BoardFillFailureKindSelectionTests.Ready));
        }
        using var receipt = JsonDocument.Parse(File.ReadAllText(outcome.ReceiptPath!));
        Assert.Equal(JsonValueKind.Null, receipt.RootElement.GetProperty("failure").ValueKind);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Store_write_failure_preserves_stale_and_records_error(bool failNote)
    {
        using var fixture = new CliAuthorDraftCommandTests.Fixture();
        var before = (await fixture.Store.GetByExactIdAsync(fixture.Item.Id))!;
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = fixture.Workspace.BacklogStorePath, Pooling = false
        }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = $"""
                CREATE TRIGGER injected_retirement_failure
                BEFORE {(failNote ? "INSERT ON backlog_notes" : "UPDATE OF tags ON backlog")}
                BEGIN SELECT RAISE(ABORT, 'injected backlog write failure'); END;
                """;
            command.ExecuteNonQuery();
        }
        const string reason = "Already implemented";
        string[] evidence = ["seed.txt:1"];
        var calls = 0;
        var outcome = AuthorBriefDraftService.Run(fixture.Item.Id, fixture.Workspace, new((_, _) =>
        {
            calls++;
            return Task.FromResult(new WorkerProcessRunResult(0,
                JsonSerializer.Serialize(new { kind = "stale", reason, evidenceReferences = evidence }), ""));
        }, fixture.Repository), fixture.Output, fixture.Error);

        Assert.Equal(1, calls);
        Assert.Equal("stale", outcome.Kind);
        Assert.Equal(2, outcome.ExitCode);
        Assert.Equal(reason + fixture.Output.NewLine, fixture.Output.ToString());
        Assert.Contains("Error: author-stale-retirement:", fixture.Error.ToString());
        Assert.Contains("injected backlog write failure", fixture.Error.ToString());
        using var receipt = fixture.Receipt();
        Assert.Equal("stale", receipt.RootElement.GetProperty("kind").GetString());
        var failure = receipt.RootElement.GetProperty("failure").GetString();
        Assert.Contains("injected backlog write failure", failure);
        Assert.Equal("Error: " + failure + fixture.Error.NewLine, fixture.Error.ToString());
        Assert.Empty(Directory.GetFiles(fixture.Drafts, "*.md"));
        var current = (await fixture.Store.GetByExactIdAsync(fixture.Item.Id))!;
        Assert.Equal(BacklogItemStatus.Open, current.Status);
        Assert.Equal(before.Tags, current.Tags);
        Assert.Equal(before.Notes.Count + (failNote ? 0 : 1), current.Notes.Count);
        Assert.All(before.Notes, note => Assert.Contains(note, current.Notes));
        if (!failNote) Assert.Contains(reason, Assert.Single(current.Notes.Except(before.Notes)).Text);
    }
}
