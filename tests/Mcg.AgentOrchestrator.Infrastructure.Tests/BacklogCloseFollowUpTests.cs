using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

// Parallel-safe: each case owns its workspace/database and uses AsyncLocal console capture.
public sealed class BacklogCloseFollowUpTests : CliCommandTestBase
{
    private readonly string root = CreateTempDirectory();
    private readonly OrchestratorWorkspace workspace;
    private readonly BacklogStore store;
    private readonly AgentOrchestratorKernel kernel = new();
    private protected override bool UsesGit => false;

    public BacklogCloseFollowUpTests()
    {
        workspace = CreateRefinedWorkspace(root);
        store = new BacklogStore(workspace.BacklogStorePath);
    }

    public override async ValueTask DisposeAsync()
    {
        try { Directory.Delete(root, recursive: true); }
        finally { await base.DisposeAsync(); }
    }

    [Xunit.Fact]
    public async Task Close_SelectsOpenCauseNeighborsAndPreservesRelatedState()
    {
        var item = await AddWithId('8', "Closing root");
        var incoming = await AddWithId('1', "Claimed open neighbor");
        var outgoing = await AddWithId('e', "Open neighbor");
        var done = await AddWithId('2', "Done neighbor");
        var superseded = await AddWithId('3', "Superseded neighbor");
        var related = await AddWithId('4', "Merely related");
        var duplicate = await AddWithId('5', "Merely duplicate");
        var secondHop = await AddWithId('6', "Second hop only");
        foreach (var other in new[] { incoming, outgoing, done, superseded })
            await store.LinkAsync(item.Id, other.Id, BacklogLinkKind.RelatedCause);
        await store.LinkAsync(item.Id, related.Id, BacklogLinkKind.Related);
        await store.LinkAsync(item.Id, duplicate.Id, BacklogLinkKind.Duplicate);
        await store.LinkAsync(incoming.Id, outgoing.Id, BacklogLinkKind.RelatedCause);
        await store.LinkAsync(incoming.Id, secondHop.Id, BacklogLinkKind.RelatedCause);
        await store.CloseAsync(done.Id);
        await store.SupersedeAsync(superseded.Id, outgoing.Id);
        await store.AppendNoteAsync(incoming.Id, "Preserve neighbor note");
        await store.UpdateAsync(incoming.Id, new BacklogItemUpdate(Priority: "P1", Tags: "keep"));
        await store.AddDependencyAsync(incoming.Id,
            new BacklogDependencyTarget(secondHop.Id, BacklogDependencyTargetKind.Backlog));
        var claim = kernel.CreateGoal("Claim an open linked item");
        kernel.SetGoalSourceBacklogItemId(claim.Id, incoming.Id);
        var loaded = (await store.GetByExactIdAsync(item.Id))!;
        Assert.Contains(loaded.Links, link => link.Item1Id == item.Id);
        Assert.Contains(loaded.Links, link => link.Item2Id == item.Id);
        var before = ReadRelatedState(item.Id);

        var output = RunClose(item);

        Assert.StartsWith($"Closed: [{item.Id}] {item.Title}{Environment.NewLine}", output);
        Assert.Contains("RelatedCause advisory", output);
        Assert.Contains($"- {incoming.Id} [Open] {incoming.Title}", output);
        Assert.Contains($"- {outgoing.Id} [Open] {outgoing.Title}", output);
        foreach (var excluded in new[] { done, superseded, related, duplicate, secondHop })
            Assert.DoesNotContain(excluded.Id, output);
        Assert.True(output.IndexOf(incoming.Id, StringComparison.Ordinal) < output.IndexOf(outgoing.Id, StringComparison.Ordinal));
        Assert.Equal(before, ReadRelatedState(item.Id));
        Assert.Equal(BacklogItemStatus.Done, (await store.GetByExactIdAsync(item.Id))!.Status);

        // A repeated close retains existing root semantics and obtains fresh neighbor evidence.
        await store.CloseAsync(outgoing.Id);
        await store.UpdateAsync(incoming.Id, new BacklogItemUpdate(Title: "Refreshed title"));
        var beforeRepeat = ReadRelatedState(item.Id);
        var repeated = RunClose(item);
        Assert.Contains($"- {incoming.Id} [Open] Refreshed title", repeated);
        Assert.DoesNotContain(outgoing.Id, repeated);
        Assert.DoesNotContain(incoming.Title, repeated);
        Assert.Equal(beforeRepeat, ReadRelatedState(item.Id));
        Assert.Equal(BacklogItemStatus.Done, (await store.GetByExactIdAsync(item.Id))!.Status);
    }

    [Xunit.Fact]
    public async Task Close_MissingNeighborIsUnavailableAndHealthyNeighborSurvives()
    {
        var item = await store.AddAsync("Root");
        var healthy = await store.AddAsync("Healthy");
        await store.LinkAsync(item.Id, healthy.Id, BacklogLinkKind.RelatedCause);
        var missing = new string('f', 32);
        using (var connection = OpenDatabase())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                INSERT INTO backlog_links (item1_id, item2_id, kind, canonical_id, created_at)
                VALUES ($root, $missing, 'RelatedCause', NULL, '2026-11-01T00:00:00+00:00')
                """;
            command.Parameters.AddWithValue("$root", item.Id);
            command.Parameters.AddWithValue("$missing", missing);
            Assert.Equal(1, command.ExecuteNonQuery());
        }

        var output = RunClose(item);

        Assert.Contains($"Closed: [{item.Id}]", output);
        Assert.Contains($"- {healthy.Id} [Open] Healthy", output);
        Assert.Contains($"- {missing} [item unavailable]", output);
        Assert.Contains("Follow-up information unavailable for 1 linked items.", output);
        Assert.DoesNotContain($"{missing} [Open]", output);
        Assert.Equal(BacklogItemStatus.Done, (await store.GetByExactIdAsync(item.Id))!.Status);
    }

    [Xunit.Theory]
    [Xunit.InlineData("root-throw")]
    [Xunit.InlineData("root-missing")]
    [Xunit.InlineData("neighbor-throw")]
    public async Task FollowUp_ReadFailuresPreserveCommittedClose(string failure)
    {
        var item = await store.AddAsync("Root");
        var healthy = await store.AddAsync("Healthy");
        var broken = await store.AddAsync("Unreadable title");
        await store.LinkAsync(item.Id, healthy.Id, BacklogLinkKind.RelatedCause);
        await store.LinkAsync(item.Id, broken.Id, BacklogLinkKind.RelatedCause);
        var closed = await store.CloseAsync(item.Id);
        var before = ReadRelatedState(item.Id);
        var calls = new List<string>();
        BacklogItem? Load(string id)
        {
            calls.Add(id);
            if (id == item.Id && failure == "root-missing") return null;
            if ((id == item.Id && failure == "root-throw") || (id == broken.Id && failure == "neighbor-throw"))
                throw new IOException("Advisory lookup failed");
            return store.GetByExactIdAsync(id).GetAwaiter().GetResult();
        }

        var text = BacklogTextView.RenderBacklogCloseFollowUp(closed.Id, Load);

        Assert.Equal(item.Id, calls[0]);
        Assert.Contains("unavailable", text);
        Assert.DoesNotContain("Unreadable title", text);
        if (failure == "neighbor-throw")
        {
            Assert.Equal(3, calls.Count);
            Assert.Contains($"- {healthy.Id} [Open] Healthy", text);
            Assert.Contains($"- {broken.Id} [item unavailable]", text);
        }
        else
        {
            Assert.Single(calls);
            Assert.Contains("Follow-up information unavailable.", text);
        }
        Assert.Equal(BacklogItemStatus.Done, (await store.GetByExactIdAsync(item.Id))!.Status);
        Assert.Equal(before, ReadRelatedState(item.Id));
    }

    [Xunit.Fact]
    public void FollowUp_UnicodePressureBoundsWholeSectionAndReportsOmissions()
    {
        var item = Item("root", "Root");
        var neighbors = Enumerable.Range(0, 20).Select(index => Item($"neighbor-{index:D2}",
            string.Concat(Enumerable.Repeat("漢😀\r\n", 500)))).ToDictionary(other => other.Id);
        item = item with { Links = neighbors.Keys.Append("zzz-unavailable").Select(id =>
            new BacklogLink(item.Id, id, BacklogLinkKind.RelatedCause, null, item.CreatedAt)).ToArray() };
        BacklogItem? Load(string id) => id == item.Id ? item : neighbors.GetValueOrDefault(id);

        var text = BacklogTextView.RenderBacklogCloseFollowUp(item.Id, Load);
        var bytes = Encoding.UTF8.GetBytes(text);
        Assert.InRange(bytes.Length, 1, 1_000);
        Assert.Equal(text, new UTF8Encoding(false, true).GetString(bytes));
        Assert.DoesNotContain("\uFFFD", text);
        Assert.Contains("RelatedCause advisory", text);
        Assert.Contains("... [title truncated]", text);
        Assert.Contains("Follow-up information unavailable for 1 linked items.", text);
        var included = Regex.Matches(text, @"(?m)^- neighbor-\d+ \[Open\] .+$");
        var omitted = Regex.Match(text, @"Omitted (\d+) items\.");
        Assert.True(included.Count > 0);
        Assert.True(omitted.Success);
        Assert.Equal(21 - included.Count, int.Parse(omitted.Groups[1].Value, CultureInfo.InvariantCulture));
        var first = text;
        item = item with { Links = item.Links.Reverse().Concat(item.Links).ToArray() };
        Assert.Equal(first, BacklogTextView.RenderBacklogCloseFollowUp(item.Id, Load));
    }

    [Xunit.Fact]
    public async Task Close_NoQualifyingNeighborsKeepsConciseReceipt()
    {
        var item = await store.AddAsync("Root");
        var other = await store.AddAsync("Merely related");
        await store.LinkAsync(item.Id, other.Id, BacklogLinkKind.Related);

        Assert.Equal($"Closed: [{item.Id}] {item.Title}{Environment.NewLine}", RunClose(item));
        Assert.Equal($"Closed: [{item.Id}] {item.Title}{Environment.NewLine}", RunClose(item));
    }

    private string RunClose(BacklogItem item) =>
        ExecuteCliAndCapture(["backlog-close", item.Id], kernel, workspace);

    private async Task<BacklogItem> AddWithId(char digit, string title)
    {
        var item = await store.AddAsync(title);
        using var connection = OpenDatabase();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE backlog SET id = $id WHERE id = $old";
        command.Parameters.AddWithValue("$id", new string(digit, 32));
        command.Parameters.AddWithValue("$old", item.Id);
        Assert.Equal(1, command.ExecuteNonQuery());
        return item with { Id = new string(digit, 32) };
    }

    private SqliteConnection OpenDatabase()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = workspace.BacklogStorePath, Pooling = false, ForeignKeys = false
        }.ToString());
        connection.Open();
        return connection;
    }

    private string[] ReadRelatedState(string closedId)
    {
        using var connection = OpenDatabase();
        var rows = new List<string>();
        foreach (var table in new[] { "backlog", "backlog_notes", "backlog_links", "backlog_dependencies", "backlog_history" })
        {
            using var command = connection.CreateCommand();
            command.CommandText = table == "backlog"
                ? "SELECT * FROM backlog WHERE id <> $closed ORDER BY id"
                : $"SELECT * FROM {table} ORDER BY rowid";
            if (table == "backlog") command.Parameters.AddWithValue("$closed", closedId);
            using var reader = command.ExecuteReader();
            while (reader.Read())
                rows.Add(table + ":" + string.Join("|", Enumerable.Range(0, reader.FieldCount).Select(i => reader.GetValue(i).ToString())));
        }
        return rows.ToArray();
    }

    private static BacklogItem Item(string id, string title) =>
        new(id, title, "", BacklogItemStatus.Open, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, null);
}
