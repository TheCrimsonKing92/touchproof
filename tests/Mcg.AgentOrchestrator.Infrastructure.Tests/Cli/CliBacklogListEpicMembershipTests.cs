using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;
using Xunit;

// Parallel-safe: each case owns its databases and workspace; console capture is AsyncLocal.
public sealed class CliBacklogListEpicMembershipTests : CliTaskQueryTestSupport, IDisposable
{
    private readonly string root = CreateTempDirectory();

    [Fact]
    public async Task Text_DirectMembership_PreservesStatusGoalAndTitle()
    {
        var seed = await SeedAsync();
        var epicId = await AssignAsync(seed);

        var output = Run(["backlog-list"], seed);

        Assert.Contains($"{seed.Dependent.Id} | status=open | goal=- | epic={epicId[..8]} Membership epic | title={seed.Dependent.Title}", output);
        Assert.Contains($"{seed.Prerequisite.Id} | status=claimed | goal={CliBacklogReadOnlyRouteTests.LinkedGoalId} | epic=none | title={seed.Prerequisite.Title}", output);
    }

    [Fact]
    public async Task Json_DirectMembership_PreservesEveryExistingField()
    {
        var seed = await SeedAsync();
        var before = Rows(Run(["backlog-list", "--json"], seed));
        var epicId = await AssignAsync(seed);

        var rows = Rows(Run(["backlog-list", "--json"], seed));

        Assert.Equal(4, rows.Count);
        Assert.Equal(before.Keys.Order(), rows.Keys.Order());
        foreach (var (id, row) in rows)
        {
            Assert.Equal(new[] { "id", "status", "goalId", "ownerAmbiguous", "eligible", "title",
                "createdAt", "updatedAt", "priority", "tags", "supersededBy", "epicId" },
                row.EnumerateObject().Select(property => property.Name));
            foreach (var property in row.EnumerateObject().Where(property => property.Name != "epicId"))
                Assert.Equal(before[id].GetProperty(property.Name).GetRawText(), property.Value.GetRawText());
            if (id == seed.Dependent.Id)
                Assert.Equal(epicId, row.GetProperty("epicId").GetString());
            else
                Assert.Equal(JsonValueKind.Null, row.GetProperty("epicId").ValueKind);
        }
        Assert.Equal("claimed", rows[seed.Prerequisite.Id].GetProperty("status").GetString());
        Assert.Equal(CliBacklogReadOnlyRouteTests.LinkedGoalId,
            rows[seed.Prerequisite.Id].GetProperty("goalId").GetString());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task NoEpic_StatusOpen_ReturnsUnassignedSubset(bool json, bool onlyOpen)
    {
        var seed = await SeedAsync();
        await AssignAsync(seed);
        string[] args = ["backlog-list", .. onlyOpen ? new[] { "--status", "open" } : [],
            .. json ? new[] { "--json" } : []];
        var baseline = Run(args, seed);

        var filtered = Run([.. args, "--no-epic"], seed);

        if (json)
        {
            var expected = Rows(baseline).Values.Where(row => row.GetProperty("epicId").ValueKind == JsonValueKind.Null);
            var rows = Rows(filtered);
            Assert.Equal(onlyOpen ? 2 : 3, rows.Count);
            Assert.Equal(expected.Select(row => row.GetRawText()), rows.Values.Select(row => row.GetRawText()));
            Assert.Equal("claimed", rows[seed.Prerequisite.Id].GetProperty("status").GetString());
        }
        else
        {
            var expected = ItemLines(baseline).Where(line => line.Contains("| epic=none |", StringComparison.Ordinal));
            var lines = ItemLines(filtered);
            Assert.Equal(onlyOpen ? 2 : 3, lines.Length);
            Assert.Equal(expected, lines);
            Assert.Contains(lines, line => line.Contains($"{seed.Prerequisite.Id} | status=claimed", StringComparison.Ordinal));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NoEpic_TextNewestLimit_FiltersBeforeTruncation(bool json)
    {
        var seed = await SeedAsync();
        var store = new PortfolioStore(seed.Workspace.PortfolioStorePath);
        var epic = await store.AddEpicAsync("Newest assigned");
        await new BacklogStore(seed.Workspace.BacklogStorePath).AddAsync("Unrelated noise", "Excluded by text filter");
        string[] orderedArgs = ["backlog-list", "--text", "Quartz", "--newest-first", "--json"];
        var ordered = Rows(Run(orderedArgs, seed)).Keys.ToArray();
        Assert.Equal(4, ordered.Length);
        await store.AssignBacklogItemToEpicAsync(ordered[0], epic.Id);
        string[] args = ["backlog-list", "--no-epic", "--all", "--text", "Quartz", "--newest-first",
            "--limit", "1", .. json ? new[] { "--json" } : []];

        var output = Run(args, seed);

        if (json)
            Assert.Equal(ordered[1], Assert.Single(Rows(output)).Key);
        else
            Assert.Contains($" {ordered[1]} |", Assert.Single(ItemLines(output)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingPortfolio_NoEpicKeepsEveryRow_WithoutCreatingStore(bool json)
    {
        var seed = await SeedAsync();
        Assert.False(File.Exists(seed.Workspace.PortfolioStorePath));
        string[] args = json ? ["backlog-list", "--json"] : ["backlog-list"];

        var before = Run(args, seed);
        var filtered = Run([.. args, "--no-epic"], seed);

        AssertAllUnassigned(before, json);
        AssertAllUnassigned(filtered, json);
        if (json)
            Assert.Equal(Rows(before).Values.Select(row => row.GetRawText()),
                Rows(filtered).Values.Select(row => row.GetRawText()));
        else
            Assert.Equal(before, filtered);
        Assert.False(File.Exists(seed.Workspace.PortfolioStorePath));
        Assert.False(File.Exists(seed.Workspace.PortfolioStorePath + "-wal"));
        Assert.False(File.Exists(seed.Workspace.PortfolioStorePath + "-shm"));
    }

    [Theory]
    [InlineData("missing", false)]
    [InlineData("older", true)]
    [InlineData("newer", false)]
    public async Task NonCurrentSchema_TreatsEveryItemAsUnassigned_WithoutUpgrade(
        string schema, bool json)
    {
        var seed = await SeedAsync();
        await AssignAsync(seed);
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = seed.Workspace.PortfolioStorePath, Mode = SqliteOpenMode.ReadWrite, Pooling = false
        }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = schema == "missing"
                ? "DELETE FROM store_schema_versions WHERE store_name = 'portfolio'"
                : "UPDATE store_schema_versions SET version = $version WHERE store_name = 'portfolio'";
            if (schema != "missing")
                command.Parameters.AddWithValue("$version", StoreSchemaRegistry.Portfolio.CurrentVersion!.Value +
                    (schema == "older" ? -1 : 1));
            Assert.Equal(1, command.ExecuteNonQuery());
        }
        var bytes = File.ReadAllBytes(seed.Workspace.PortfolioStorePath);
        string[] args = json ? ["backlog-list", "--json"] : ["backlog-list"];

        AssertAllUnassigned(Run(args, seed), json);
        AssertAllUnassigned(Run([.. args, "--no-epic"], seed), json);

        Assert.Equal(bytes, File.ReadAllBytes(seed.Workspace.PortfolioStorePath));
    }

    [Fact]
    public async Task CorruptPortfolio_Listing_PropagatesSqliteFailure()
    {
        var seed = await SeedAsync();
        File.WriteAllText(seed.Workspace.PortfolioStorePath, "not a SQLite database");

        Assert.Throws<SqliteException>(() => Run(["backlog-list", "--no-epic"], seed));

        Assert.Equal("not a SQLite database", File.ReadAllText(seed.Workspace.PortfolioStorePath));
    }

    private async Task<CliBacklogReadOnlyRouteTests.Seed> SeedAsync()
    {
        var seed = await CliBacklogReadOnlyRouteTests.CreateSeedAsync(root);
        var store = new BacklogStore(seed.Workspace.BacklogStorePath);
        await store.UpdateAsync(seed.Prerequisite.Id, new BacklogItemUpdate(Priority: "high", Tags: "cli,epic"));
        await store.AddAsync("Quartz unassigned open", "Still open");
        var closed = await store.AddAsync("Quartz unassigned closed", "Closed explicitly");
        await store.CloseAsync(closed.Id);
        Assert.Equal(4, (await store.ListAsync(includeAll: true)).Count);
        return seed;
    }

    private static async Task<string> AssignAsync(CliBacklogReadOnlyRouteTests.Seed seed)
    {
        var store = new PortfolioStore(seed.Workspace.PortfolioStorePath);
        var epic = await store.AddEpicAsync("Membership epic");
        await store.AssignBacklogItemToEpicAsync(seed.Dependent.Id, epic.Id);
        // The claimed prerequisite must not inherit its owning goal's epic.
        await store.AssignGoalToEpicAsync(CliBacklogReadOnlyRouteTests.LinkedGoalId, epic.Id);
        return epic.Id;
    }

    private static void AssertAllUnassigned(string output, bool json)
    {
        if (json)
        {
            var rows = Rows(output);
            Assert.Equal(4, rows.Count);
            Assert.All(rows.Values, row => Assert.Equal(JsonValueKind.Null, row.GetProperty("epicId").ValueKind));
        }
        else
        {
            var lines = ItemLines(output);
            Assert.Equal(4, lines.Length);
            Assert.All(lines, line => Assert.Contains("| epic=none |", line));
        }
    }

    private static string[] ItemLines(string output) =>
        output.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
            .Where(line => line.Contains(" | status=", StringComparison.Ordinal)).ToArray();

    private static Dictionary<string, JsonElement> Rows(string output)
    {
        using var json = JsonDocument.Parse(output);
        return json.RootElement.GetProperty("payload").GetProperty("items").EnumerateArray()
            .ToDictionary(row => row.GetProperty("id").GetString()!, row => row.Clone());
    }

    private static string Run(string[] args, CliBacklogReadOnlyRouteTests.Seed seed)
    {
        CliCommandHelp.ThrowIfInvalidFlags(args);
        Assert.True(CliReadOnlyCommandRunner.IsReadOnlyCommand(args));
        var repository = new ProbeStateRepository(seed.Kernel) { ThrowOnOutbox = true };
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var profiles = WorkerProfileCatalog.Default();
        Goal? current = null;
        var output = CaptureConsole(() =>
        {
            Assert.True(CliReadOnlyCommandRunner.TryExecute(args, repository, seed.Workspace,
                new InMemoryModelProviderRegistry([]), ref agents, ref profiles, ref current, out var changed));
            Assert.False(changed);
        });
        Assert.Equal(0, repository.FullLoadAttempts + repository.ListGoalMetadataCount +
            repository.SaveAttempts + repository.MergeSaveAttempts + repository.MutationAttempts +
            repository.ListOutboxMessagesCount + repository.OutboxClaimAttempts);
        return output;
    }

    public void Dispose() => Directory.Delete(root, recursive: true);
}
