using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;
using Xunit;

// Private SQLite/portfolio roots, fixed clock and AsyncLocal console capture isolate each case.
public sealed class CliAttentionUnlinkedGoalJsonTests : CliTaskQueryTestSupport
{
    private static readonly DateTimeOffset Now = new(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("attention", "--json")]
    [InlineData("attention", "list", "--json")]
    public async Task FullForms_MissingBoth_ReportsUnloadedGoal(params string[] args)
    {
        using var fixture = await Fixture.CreateAsync(new SeedGoal("one", "Unlinked goal"));
        var context = await AttentionJsonReadContext.ReadAsync(fixture.Queries, fixture.Workspace, Now);
        Assert.Empty(context.Goals!); // No wait/item caused this goal to be hydrated.
        Assert.Single(context.GoalFacts!);
        Assert.False(File.Exists(fixture.Workspace.PortfolioStorePath));

        using var json = JsonDocument.Parse(fixture.Read(args));
        var row = Assert.Single(json.RootElement.GetProperty("payload").GetProperty("unlinkedGoals").EnumerateArray());
        Assert.Equal(new[] { "goalId", "title", "reason" }, row.EnumerateObject().Select(field => field.Name));
        Assert.Equal("one", row.GetProperty("goalId").GetString());
        Assert.Equal("Unlinked goal", row.GetProperty("title").GetString());
        Assert.Equal("no-epic-no-backlog-link", row.GetProperty("reason").GetString());
        Assert.False(File.Exists(fixture.Workspace.PortfolioStorePath));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t ")]
    public async Task FullForm_LinkCombinations_UsesIndexedBacklogPresence(string? missingLink)
    {
        using var fixture = await Fixture.CreateAsync(
            new("a", "Epic only", Backlog: missingLink),
            new("b", "Backlog only", Backlog: "unresolved-backlog-id"),
            new("c", "Both links", Backlog: "another-backlog-id"));
        await fixture.AssignEpicAsync("a", "c");
        // Snapshots deliberately have no backlog id; the indexed column is authoritative here.
        var facts = (await fixture.Queries.ReadAttentionProjectionAsync())!.Goals;
        Assert.Equal("unresolved-backlog-id", facts.Single(goal => goal.GoalId == "b").SourceBacklogItemId);
        using var json = JsonDocument.Parse(fixture.Read(["attention", "--json"]));
        var rows = json.RootElement.GetProperty("payload").GetProperty("unlinkedGoals").EnumerateArray().ToArray();
        Assert.Equal(new[] { "a", "b" }, rows.Select(row => row.GetProperty("goalId").GetString()));
        Assert.Equal(new[] { "no-backlog-link", "no-epic" }, rows.Select(row => row.GetProperty("reason").GetString()));
        Assert.Equal(new[] { "Epic only", "Backlog only" }, rows.Select(row => row.GetProperty("title").GetString()));
    }

    [Fact]
    public async Task SummaryForms_UnlinkedState_PreservesFullRows()
    {
        using var fixture = await Fixture.CreateAsync(new("b", "Second"), new("a", "First"));
        await fixture.AssignEpicAsync("b");
        using var full = JsonDocument.Parse(fixture.Read(["attention", "--json"]));
        var expected = full.RootElement.GetProperty("payload").GetProperty("unlinkedGoals");
        Assert.Equal(new[] { "a", "b" }, expected.EnumerateArray().Select(row => row.GetProperty("goalId").GetString()));
        foreach (var args in SummaryForms())
        {
            using var summary = JsonDocument.Parse(fixture.Read(args));
            Assert.Equal(expected.GetRawText(), summary.RootElement.GetProperty("payload").GetProperty("unlinkedGoals").GetRawText());
        }
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("linked")]
    [InlineData("excluded")]
    public async Task CleanState_AllForms_PreservesPreChangeBytes(string state)
    {
        SeedGoal[] seeds = state switch
        {
            "empty" => [],
            "linked" => [new("linked", "Linked", Backlog: "backlog-id")],
            _ => new[] { GoalStatus.Completed, GoalStatus.Failed, GoalStatus.Cancelled, GoalStatus.Superseded, GoalStatus.Parked }
                .Select(status => new SeedGoal(status.ToString(), "Excluded", status)).ToArray()
        };
        using var fixture = await Fixture.CreateAsync(seeds);
        if (state == "linked") await fixture.AssignEpicAsync("linked");
        foreach (var args in new[] { new[] { "attention", "--json" }, ["attention", "list", "--json"] }.Concat(SummaryForms()))
        {
            var expected = args.Contains("--summary")
                ? AttentionAgentReadView.RenderSummary([], [], new FixedTime())
                : AttentionAgentReadView.Render([], [], new FixedTime());
            var actual = fixture.Read(args).TrimEnd('\r', '\n');
            Assert.Equal(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(actual));
            using var json = JsonDocument.Parse(actual);
            Assert.False(json.RootElement.GetProperty("payload").TryGetProperty("unlinkedGoals", out _));
        }
    }

    [Fact]
    public async Task LegacyFallback_UnlinkedGoal_MatchesProjection()
    {
        using var fixture = await Fixture.CreateAsync(new SeedGoal("one", "Fallback goal"));
        var legacy = new LegacyQueries(fixture.Queries);
        foreach (var args in new[] { new[] { "attention", "--json" } }.Concat(SummaryForms()))
        {
            var expected = fixture.Read(args);
            using var json = JsonDocument.Parse(expected);
            Assert.Single(json.RootElement.GetProperty("payload").GetProperty("unlinkedGoals").EnumerateArray());
            Assert.Equal(expected, fixture.Read(args, legacy));
        }
    }

    [Fact]
    public async Task RowBuilder_DuplicateGoalId_UsesFirstOccurrence()
    {
        using var fixture = await Fixture.CreateAsync();
        var rows = UnlinkedGoalAttentionView.Rows([
            new("same", GoalStatus.Active, 1, Title: "First"),
            new("same", GoalStatus.Active, 1, SourceBacklogItemId: "link", Title: "Second")], fixture.Workspace.PortfolioStorePath);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(rows));
        var row = Assert.Single(json.RootElement.EnumerateArray());
        Assert.Equal("First", row.GetProperty("title").GetString());
        Assert.Equal("no-epic-no-backlog-link", row.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task FullForm_CorruptPortfolio_PropagatesLoaderFailure()
    {
        using var fixture = await Fixture.CreateAsync(new SeedGoal("one", "Goal"));
        await File.WriteAllTextAsync(fixture.Workspace.PortfolioStorePath, "not a SQLite database");
        Assert.True(File.Exists(fixture.Workspace.PortfolioStorePath));
        var failure = Assert.Throws<SqliteException>(() => fixture.Read(["attention", "--json"]));
        Assert.Equal(26, failure.SqliteErrorCode);
    }

    private static string[][] SummaryForms() =>
        [["attention", "list", "--summary", "--json"], ["attention", "list", "--json", "--summary"]];
    private sealed class FixedTime : TimeProvider { public override DateTimeOffset GetUtcNow() => Now; }
    private sealed record SeedGoal(string Id, string Title, GoalStatus Status = GoalStatus.Active, string? Backlog = null);

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = CreateTempDirectory();
        public OrchestratorWorkspace Workspace { get; }
        public IOrchestratorStateQueries Queries => SqliteOrchestratorStateRepository.OpenReadOnly(Workspace.SqliteStatePath);
        private Fixture()
        {
            Workspace = OrchestratorWorkspace.ForDirectory(_root);
            StateDbMigrations.EnsureUpToDate(Workspace.SqliteStatePath);
        }
        public static async Task<Fixture> CreateAsync(params SeedGoal[] seeds)
        {
            var fixture = new Fixture();
            try
            {
                var kernel = new AgentOrchestratorKernel();
                foreach (var seed in seeds) kernel.CreateGoal(new GoalId(seed.Id), seed.Title);
                await new SqliteOrchestratorStateRepository(fixture.Workspace.SqliteStatePath).SaveAsync(kernel);
                await using var connection = new SqliteConnection($"Data Source={fixture.Workspace.SqliteStatePath};Pooling=False");
                await connection.OpenAsync();
                var options = new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } };
                foreach (var seed in seeds)
                {
                    var snapshot = kernel.ExportSnapshot().Goals.Single(goal => goal.Id == seed.Id) with { Status = seed.Status };
                    await using var update = connection.CreateCommand();
                    update.CommandText = "UPDATE goals SET status = $status, source_backlog_item_id = $backlog, snapshot_json = $json WHERE id = $id";
                    update.Parameters.AddWithValue("$id", seed.Id);
                    update.Parameters.AddWithValue("$status", seed.Status.ToString());
                    update.Parameters.AddWithValue("$backlog", (object?)seed.Backlog ?? DBNull.Value);
                    update.Parameters.AddWithValue("$json", JsonSerializer.Serialize(snapshot, options));
                    Assert.Equal(1, await update.ExecuteNonQueryAsync());
                }
                return fixture;
            }
            catch { fixture.Dispose(); throw; }
        }
        public async Task AssignEpicAsync(params string[] goalIds)
        {
            var store = new PortfolioStore(Workspace.PortfolioStorePath);
            var epic = await store.AddEpicAsync("Epic");
            foreach (var goalId in goalIds) await store.AssignGoalToEpicAsync(goalId, epic.Id);
        }
        public string Read(string[] args, IOrchestratorStateQueries? queries = null) => CaptureConsole(() =>
            CliAttentionJsonQueryCommand.Execute(args, queries ?? Queries, Workspace, new FixedTime()));
        public void Dispose() => Directory.Delete(_root, true);
    }

    private sealed class LegacyQueries(IOrchestratorStateQueries inner) : IOrchestratorStateQueries
    {
        public Task<IReadOnlyList<GoalSummary>> ListGoalMetadataAsync(CancellationToken cancellationToken = default) =>
            inner.ListGoalMetadataAsync(cancellationToken);
        public Task<AgentOrchestratorKernel> LoadGoalsAsync(IReadOnlyCollection<GoalId> goalIds, CancellationToken cancellationToken = default) =>
            inner.LoadGoalsAsync(goalIds, cancellationToken);
    }
}
