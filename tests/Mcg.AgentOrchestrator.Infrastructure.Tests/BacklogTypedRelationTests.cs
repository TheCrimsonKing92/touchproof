using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

// Parallel-safe: each test owns its workspace/database and uses AsyncLocal console capture.
public sealed class BacklogTypedRelationTests : CliCommandTestBase
{
    private readonly string root = CreateTempDirectory();
    private readonly OrchestratorWorkspace workspace;
    private readonly BacklogStore store;
    private readonly AgentOrchestratorKernel kernel = new();

    private protected override bool UsesGit => false;

    public BacklogTypedRelationTests()
    {
        workspace = CreateRefinedWorkspace(root);
        store = new BacklogStore(workspace.BacklogStorePath);
    }

    public override async ValueTask DisposeAsync()
    {
        try { Directory.Delete(root, recursive: true); }
        finally { await base.DisposeAsync(); }
    }

    [Xunit.Theory]
    [Xunit.InlineData(null, BacklogLinkKind.Duplicate, "link-duplicate", "Linked duplicate")]
    [Xunit.InlineData("--related", BacklogLinkKind.Related, "link-related", "Linked related")]
    [Xunit.InlineData("--related-cause", BacklogLinkKind.RelatedCause, "link-related-cause", "Linked related-cause")]
    public async Task Link_EachKind_PersistsAndShowsBothEndpoints(
        string? flag, BacklogLinkKind kind, string action, string message)
    {
        var pair = new[] { await store.AddAsync("First title"), await store.AddAsync("Second title") }
            .OrderByDescending(item => item.Id, StringComparer.Ordinal).ToArray();
        var first = pair[0];
        var second = await store.CloseAsync(pair[1].Id);

        var output = Link(first, second, flag);

        var link = Assert.Single((await store.GetByExactIdAsync(first.Id))!.Links);
        Assert.Equal(kind, link.Kind);
        Assert.Equal(second.Id, link.Item1Id);
        Assert.Equal(first.Id, link.Item2Id);
        Assert.Equal(kind == BacklogLinkKind.Duplicate ? first.Id : null, link.CanonicalId);
        var outputFirst = kind == BacklogLinkKind.RelatedCause ? first.Id : second.Id;
        var outputSecond = kind == BacklogLinkKind.RelatedCause ? second.Id : first.Id;
        Assert.Equal($"{message}: [{outputFirst[..8]}] <-> [{outputSecond[..8]}]{Environment.NewLine}", output);
        Assert.Equal(new[] { kind.ToString() }, ReadRows("SELECT kind FROM backlog_links"));
        Assert.Equal(new[] { first.Id, second.Id }.OrderBy(id => id, StringComparer.Ordinal),
            ReadRows("SELECT backlog_item_id FROM backlog_history WHERE action = '" + action + "' ORDER BY backlog_item_id"));
        AssertRelation(first, second, kind);
        AssertRelation(second, first, kind);
    }

    [Xunit.Theory]
    [Xunit.InlineData(null)]
    [Xunit.InlineData("--related")]
    [Xunit.InlineData("--related-cause")]
    public async Task Link_SameKindRepeatedInEitherOrder_LeavesRowsUnchanged(string? flag)
    {
        var first = await store.AddAsync("First");
        var second = await store.AddAsync("Second");
        Link(first, second, flag);
        var links = ReadLinks();
        var history = ReadHistory();
        Assert.Single(links);
        Assert.Equal(2, history.Length);

        Link(first, second, flag);
        var reversedOutput = Link(second, first, flag);

        Assert.Equal(links, ReadLinks());
        Assert.Equal(history, ReadHistory());
        if (flag == "--related-cause")
            Assert.Equal($"Linked related-cause: [{second.Id[..8]}] <-> [{first.Id[..8]}]{Environment.NewLine}", reversedOutput);
    }

    [Xunit.Theory]
    [Xunit.InlineData(null, "--related-cause", "Duplicate")]
    [Xunit.InlineData("--related", null, "Related")]
    [Xunit.InlineData("--related-cause", "--related", "RelatedCause")]
    public async Task Link_DifferentKind_RefusesWithoutChangingRows(
        string? existingFlag, string? requestedFlag, string existingKind)
    {
        var first = await store.AddAsync("First");
        var second = await store.AddAsync("Second");
        Link(first, second, existingFlag);
        var links = ReadLinks();
        var history = ReadHistory();

        var error = Assert.Throws<InvalidOperationException>(() => Link(second, first, requestedFlag));

        Assert.Contains($"already linked as {existingKind}", error.Message);
        Assert.Equal(links, ReadLinks());
        Assert.Equal(history, ReadHistory());
    }

    [Xunit.Fact]
    public async Task Link_UnknownId_UsesExistingErrorAndWritesNothing()
    {
        var first = await store.AddAsync("First");

        var error = Assert.Throws<InvalidOperationException>(() =>
            Run("backlog-link", first.Id[..8], "zzzzzzzz", "--related-cause"));

        Assert.Equal("No backlog item found with id prefix 'zzzzzzzz'.", error.Message);
        Assert.Empty(ReadLinks());
        Assert.Empty(ReadHistory());
    }

    [Xunit.Fact]
    public async Task Link_SameItem_UsesExistingErrorAndWritesNothing()
    {
        var first = await store.AddAsync("First");

        var error = Assert.Throws<InvalidOperationException>(() => Link(first, first, "--related-cause"));

        Assert.Equal("A backlog item cannot be linked to itself.", error.Message);
        Assert.Empty(ReadLinks());
        Assert.Empty(ReadHistory());
    }

    [Xunit.Theory]
    [Xunit.InlineData("--related", "--related-cause")]
    [Xunit.InlineData("--related-cause", "--related")]
    public async Task Link_BothFlags_ReportsUsageBeforeResolvingIds(string firstFlag, string secondFlag)
    {
        var first = await store.AddAsync("First");

        var error = Assert.Throws<ArgumentException>(() =>
            Run("backlog-link", first.Id[..8], "zzzzzzzz", firstFlag, secondFlag));

        Assert.Equal(CliCommandHelp.BacklogLinkUsage, error.Message);
        Assert.Empty(ReadLinks());
        Assert.Empty(ReadHistory());
    }

    [Xunit.Fact]
    public async Task Show_LegacyRows_ListsBothKindsFromBothEndpoints()
    {
        var first = await store.AddAsync("Legacy first");
        var duplicate = await store.AddAsync("Legacy duplicate");
        var related = await store.AddAsync("Legacy related");
        // Raw pre-change-format rows exercise compatibility independently of the new writer.
        InsertRawLink(first.Id, duplicate.Id, "Duplicate", first.Id);
        InsertRawLink(first.Id, related.Id, "Related");
        var before = ReadLinks();

        AssertRelation(first, duplicate, BacklogLinkKind.Duplicate);
        AssertRelation(duplicate, first, BacklogLinkKind.Duplicate);
        AssertRelation(first, related, BacklogLinkKind.Related);
        AssertRelation(related, first, BacklogLinkKind.Related);
        Assert.Equal(2, (await store.GetByExactIdAsync(first.Id))!.Links.Count);
        Assert.Equal(before, ReadLinks());
        Assert.Empty(ReadHistory());
    }

    [Xunit.Theory]
    [Xunit.InlineData("FutureKind")]
    [Xunit.InlineData("7")]
    [Xunit.InlineData("related")]
    [Xunit.InlineData("Duplicate, Related")]
    [Xunit.InlineData("")]
    public async Task Show_UnknownKind_SkipsRowInEveryLoadPathAndKeepsStoredData(string kind)
    {
        var first = await store.AddAsync("First");
        var recognized = await store.AddAsync("Recognized");
        var unknown = await store.AddAsync("Unknown");
        InsertRawLink(first.Id, recognized.Id, "Related");
        InsertRawLink(first.Id, unknown.Id, kind);
        var before = ReadLinks();

        AssertRelation(first, recognized, BacklogLinkKind.Related);
        Assert.DoesNotContain(unknown.Id, Run("backlog-show", first.Id[..8]));
        Assert.DoesNotContain("Relations:", Run("backlog-show", unknown.Id[..8]));
        Assert.Single((await store.GetByExactIdAsync(first.Id))!.Links);
        Assert.Single((await store.GetByIdPrefixAsync(first.Id[..8]))!.Links);
        var listed = await store.ListAsync(includeAll: true);
        Assert.Single(listed.Single(item => item.Id == first.Id).Links);
        Assert.Empty(listed.Single(item => item.Id == unknown.Id).Links);
        Assert.Equal(before, ReadLinks());
        Assert.Empty(ReadHistory());

        var error = Assert.Throws<InvalidOperationException>(() => Link(first, unknown, "--related-cause"));
        Assert.EndsWith($"already linked as {kind}.", error.Message);
        Assert.Equal(before, ReadLinks());
        Assert.Empty(ReadHistory());
    }

    [Xunit.Fact]
    public async Task Show_Relations_AreSortedBetweenDependentsAndBody()
    {
        var first = await store.AddAsync("First", "Body after relations");
        var second = await store.AddAsync("Second");
        var third = await store.AddAsync("Third");
        Link(first, second, "--related-cause");
        Link(first, third, "--related");
        await store.AddDependencyAsync(second.Id,
            new BacklogDependencyTarget(first.Id, BacklogDependencyTargetKind.Backlog));

        var output = Run("backlog-show", first.Id[..8]);

        var relations = output.IndexOf("Relations:", StringComparison.Ordinal);
        Assert.Contains("Dependents:", output);
        Assert.Contains("Relations:", output);
        AssertRelation(first, second, BacklogLinkKind.RelatedCause);
        AssertRelation(first, third, BacklogLinkKind.Related);
        Assert.True(output.IndexOf("Dependents:", StringComparison.Ordinal) < relations);
        Assert.True(relations < output.IndexOf(first.Body, StringComparison.Ordinal));
        var ids = new[] { second.Id, third.Id }.OrderBy(id => id, StringComparer.Ordinal).ToArray();
        Assert.True(output.IndexOf(ids[0], relations, StringComparison.Ordinal) <
            output.IndexOf(ids[1], relations, StringComparison.Ordinal));
    }

    [Xunit.Fact]
    public async Task Show_MissingCounterpart_PrintsFallbackAndOtherRelations()
    {
        var first = await store.AddAsync("First");
        var second = await store.AddAsync("Second");
        const string missing = "ffffffffffffffffffffffffffffffff";
        InsertRawLink(first.Id, missing, "RelatedCause");
        Link(first, second, "--related");

        var output = Run("backlog-show", first.Id[..8]);

        Assert.Contains($"- RelatedCause {missing} [item unavailable]", output);
        Assert.Contains($"- Related {second.Id} [Open] {second.Title}", output);
    }

    [Xunit.Fact]
    public async Task Show_NoLinks_OmitsRelationsSection()
    {
        var first = await store.AddAsync("First");

        Assert.DoesNotContain("Relations:", Run("backlog-show", first.Id[..8]));
    }

    [Xunit.Fact]
    public async Task Link_SecondHistoryWriteFails_RollsBackLinkAndBothHistoryRows()
    {
        var first = await store.AddAsync("First");
        var second = await store.AddAsync("Second");
        using (var connection = OpenDatabase())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                CREATE TRIGGER refuse_second_history BEFORE INSERT ON backlog_history
                WHEN (SELECT COUNT(*) FROM backlog_history) = 1
                BEGIN SELECT RAISE(ABORT, 'history write refused'); END
                """;
            command.ExecuteNonQuery();
        }

        var error = Assert.Throws<SqliteException>(() => Link(first, second, "--related-cause"));

        Assert.Contains("history write refused", error.Message);
        Assert.Empty(ReadLinks());
        Assert.Empty(ReadHistory());
    }

    [Xunit.Fact]
    public async Task RenderRelations_LookupFailure_PrintsFallbackButPropagatesCancellation()
    {
        var first = await store.AddAsync("First");
        var second = await store.AddAsync("Second");
        Link(first, second, "--related-cause");
        var loaded = (await store.GetByExactIdAsync(first.Id))!;

        var text = BacklogTextView.RenderBacklogRelations(loaded, _ => throw new IOException("Read failed"));

        Assert.Contains($"- RelatedCause {second.Id} [item unavailable]", text);
        Assert.Throws<OperationCanceledException>(() =>
            BacklogTextView.RenderBacklogRelations(loaded, _ => throw new OperationCanceledException()));
    }

    private string Run(params string[] parts) => ExecuteCliAndCapture(parts, kernel, workspace);

    private string Link(BacklogItem first, BacklogItem second, string? flag) => flag is null
        ? Run("backlog-link", first.Id[..8], second.Id[..8])
        : Run("backlog-link", first.Id[..8], second.Id[..8], flag);

    private void AssertRelation(BacklogItem item, BacklogItem other, BacklogLinkKind kind)
    {
        var output = Run("backlog-show", item.Id[..8]);
        var lines = output.Split(Environment.NewLine);
        Assert.Contains("Relations:", lines);
        Assert.Contains($"- {kind} {other.Id} [{other.Status}] {other.Title}", lines);
    }

    private SqliteConnection OpenDatabase()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = workspace.BacklogStorePath,
            Pooling = false,
            ForeignKeys = false
        }.ToString());
        connection.Open();
        return connection;
    }

    private void InsertRawLink(string first, string second, string kind, string? canonical = null)
    {
        using var connection = OpenDatabase();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO backlog_links (item1_id, item2_id, kind, canonical_id, created_at)
            VALUES ($first, $second, $kind, $canonical, $at)
            """;
        var ordered = new[] { first, second }.OrderBy(id => id, StringComparer.Ordinal).ToArray();
        command.Parameters.AddWithValue("$first", ordered[0]);
        command.Parameters.AddWithValue("$second", ordered[1]);
        command.Parameters.AddWithValue("$kind", kind);
        command.Parameters.AddWithValue("$canonical", (object?)canonical ?? DBNull.Value);
        command.Parameters.AddWithValue("$at", "2026-11-01T00:00:00.0000000+00:00");
        Assert.Equal(1, command.ExecuteNonQuery());
    }

    private string[] ReadLinks() => ReadRows("SELECT * FROM backlog_links ORDER BY item1_id, item2_id");

    private string[] ReadHistory() => ReadRows("SELECT * FROM backlog_history ORDER BY id");

    private string[] ReadRows(string sql)
    {
        using var connection = OpenDatabase();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var rows = new List<string>();
        while (reader.Read())
            rows.Add(string.Join("|", Enumerable.Range(0, reader.FieldCount).Select(i => reader.GetValue(i).ToString())));
        return rows.ToArray();
    }
}
