using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;
using Xunit;

// Parallel-safe: each case owns its stores and kernel; console capture is AsyncLocal.
public sealed class CliGoalsEpicMembershipTests : CliTaskQueryTestSupport, IDisposable
{
    private readonly string root = CreateTempDirectory();
    private const string AssignedId = "abc10000aaaaaaaaaaaaaaaaaaaaaaaa";
    private const string UnassignedId = "abc20000bbbbbbbbbbbbbbbbbbbbbbbb";

    [Fact]
    public async Task Text_DirectMembership_PreservesGoalFields()
    {
        var (workspace, kernel) = Seed();
        var epicId = await AssignAsync(workspace);

        var output = Run(["goals"], workspace, kernel);

        Assert.Contains($"{AssignedId[..8]} Draft: epic={epicId[..8]} Assigned objective{Environment.NewLine}", output);
        Assert.Contains($"{UnassignedId[..8]} Draft: epic=none Unassigned objective{Environment.NewLine}", output);
        Assert.DoesNotContain("Membership epic", output);
    }

    [Fact]
    public async Task Json_DirectMembership_PreservesExistingRowValues()
    {
        var (workspace, kernel) = Seed();
        var before = Rows(Run(["goals", "--json"], workspace, kernel));
        var epicId = await AssignAsync(workspace);

        var rows = Rows(Run(["goals", "--json"], workspace, kernel));

        Assert.Equal(new[] { AssignedId, UnassignedId }.Order(), rows.Keys.Order());
        foreach (var (id, row) in rows)
        {
            Assert.Equal(new[] { "id", "status", "condition", "objective", "epicId" },
                row.EnumerateObject().Select(property => property.Name));
            foreach (var property in row.EnumerateObject().Where(property => property.Name != "epicId"))
                Assert.Equal(before[id].GetProperty(property.Name).GetRawText(), property.Value.GetRawText());
            Assert.Equal("Draft", row.GetProperty("status").GetString());
            Assert.Equal(JsonValueKind.Null, row.GetProperty("condition").ValueKind);
        }
        Assert.Equal(epicId, rows[AssignedId].GetProperty("epicId").GetString());
        Assert.Equal(JsonValueKind.Null, rows[UnassignedId].GetProperty("epicId").ValueKind);
    }

    [Theory]
    [InlineData("goals", "--no-epic")]
    [InlineData("goals", "--no-epic", "--json")]
    [InlineData("goals", "--json", "--no-epic")]
    [InlineData("GOALS", "--NO-EPIC", "--JSON")]
    public async Task NoEpic_CompatibleForms_ReturnOnlyDirectlyUnassigned(params string[] args)
    {
        var (workspace, kernel) = Seed();
        await AssignAsync(workspace);

        var output = Run(args, workspace, kernel);

        if (args.Contains("--json", StringComparer.OrdinalIgnoreCase))
        {
            var row = Assert.Single(Rows(output));
            Assert.Equal(UnassignedId, row.Key);
            Assert.Equal(JsonValueKind.Null, row.Value.GetProperty("epicId").ValueKind);
        }
        else
            Assert.Equal($"{UnassignedId[..8]} Draft: epic=none Unassigned objective{Environment.NewLine}", output);
    }

    [Theory]
    [InlineData("goals", "--no-epic", "--epic", "Membership epic")]
    [InlineData("goals", "--epic", "Membership epic", "--no-epic")]
    [InlineData("goals", "--no-epic", "--epic=Membership epic")]
    [InlineData("goals", "--epic=Membership epic", "--no-epic")]
    [InlineData("goals", "--no-epic", "--no-epic")]
    [InlineData("goals", "--no-epic", "--json", "--json")]
    [InlineData("goals", "--no-epic", "extra")]
    public void NoEpic_InvalidCompanions_RejectBeforeMetadataRead(params string[] args)
    {
        var repository = new ProbeStateRepository(new AgentOrchestratorKernel()) { ThrowOnOutbox = true };
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        // A corrupt store proves argument rejection also precedes portfolio reads.
        Directory.CreateDirectory(workspace.OrchestratorDirectory);
        File.WriteAllText(workspace.PortfolioStorePath, "not a SQLite database");
        Assert.True(CliReadOnlyCommandRunner.IsReadOnlyCommand(args));

        var error = Assert.Throws<ArgumentException>(() => Execute(args, workspace, repository));

        Assert.Contains(CliCommandHelp.GoalsUsage, error.Message);
        Assert.Equal(0, repository.ListGoalMetadataCount);
        AssertNoStateEffects(repository);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingPortfolio_NoEpicKeepsEveryRow_WithoutCreatingStore(bool json)
    {
        var (workspace, kernel) = Seed();
        Assert.False(File.Exists(workspace.PortfolioStorePath));
        string[] args = json ? ["goals", "--json"] : ["goals"];

        var before = Run(args, workspace, kernel);
        var filtered = Run([.. args, "--no-epic"], workspace, kernel);

        AssertAllUnassigned(before, json);
        AssertAllUnassigned(filtered, json);
        if (json)
            Assert.Equal(Rows(before).Select(pair => pair.Value.GetRawText()),
                Rows(filtered).Select(pair => pair.Value.GetRawText()));
        else
            Assert.Equal(before, filtered);
        Assert.False(File.Exists(workspace.PortfolioStorePath));
        Assert.False(File.Exists(workspace.PortfolioStorePath + "-wal"));
        Assert.False(File.Exists(workspace.PortfolioStorePath + "-shm"));
    }

    [Theory]
    [InlineData("missing", false)]
    [InlineData("older", false)]
    [InlineData("newer", true)]
    public async Task NonCurrentSchema_TreatsEveryGoalAsUnassigned_WithoutUpgrade(
        string schema, bool json)
    {
        var (workspace, kernel) = Seed();
        await AssignAsync(workspace);
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = workspace.PortfolioStorePath, Mode = SqliteOpenMode.ReadWrite, Pooling = false
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
        var bytes = File.ReadAllBytes(workspace.PortfolioStorePath);
        string[] args = json ? ["goals", "--json"] : ["goals"];

        AssertAllUnassigned(Run(args, workspace, kernel), json);
        AssertAllUnassigned(Run([.. args, "--no-epic"], workspace, kernel), json);

        Assert.Equal(bytes, File.ReadAllBytes(workspace.PortfolioStorePath));
    }

    [Fact]
    public void CorruptPortfolio_Listing_PropagatesSqliteFailure()
    {
        var (workspace, kernel) = Seed();
        Directory.CreateDirectory(workspace.OrchestratorDirectory);
        File.WriteAllText(workspace.PortfolioStorePath, "not a SQLite database");

        Assert.Throws<SqliteException>(() => Run(["goals", "--no-epic"], workspace, kernel));

        Assert.Equal("not a SQLite database", File.ReadAllText(workspace.PortfolioStorePath));
    }

    private (OrchestratorWorkspace Workspace, AgentOrchestratorKernel Kernel) Seed()
    {
        var kernel = new AgentOrchestratorKernel();
        kernel.CreateGoal(new GoalId(AssignedId), "Assigned objective");
        kernel.CreateGoal(new GoalId(UnassignedId), "Unassigned objective");
        return (OrchestratorWorkspace.ForDirectory(root), kernel);
    }

    private static async Task<string> AssignAsync(OrchestratorWorkspace workspace)
    {
        var store = new PortfolioStore(workspace.PortfolioStorePath);
        var epic = await store.AddEpicAsync("Membership epic");
        await store.AssignGoalToEpicAsync(AssignedId, epic.Id);
        // The same string in the backlog namespace must not assign this goal.
        await store.AssignBacklogItemToEpicAsync(UnassignedId, epic.Id);
        return epic.Id;
    }

    private static void AssertAllUnassigned(string output, bool json)
    {
        if (json)
        {
            var rows = Rows(output);
            Assert.Equal(new[] { AssignedId, UnassignedId }.Order(), rows.Keys.Order());
            Assert.All(rows.Values, row => Assert.Equal(JsonValueKind.Null, row.GetProperty("epicId").ValueKind));
        }
        else
        {
            var lines = output.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal(2, lines.Length);
            Assert.All(lines, line => Assert.Contains(": epic=none ", line));
        }
    }

    private static Dictionary<string, JsonElement> Rows(string output)
    {
        using var json = JsonDocument.Parse(output);
        return json.RootElement.GetProperty("payload").GetProperty("goals").EnumerateArray()
            .ToDictionary(row => row.GetProperty("id").GetString()!, row => row.Clone());
    }

    private static string Run(string[] args, OrchestratorWorkspace workspace, AgentOrchestratorKernel kernel)
    {
        CliCommandHelp.ThrowIfInvalidFlags(args);
        Assert.True(CliReadOnlyCommandRunner.IsReadOnlyCommand(args));
        var repository = new ProbeStateRepository(kernel) { ThrowOnOutbox = true };
        var output = Execute(args, workspace, repository);
        Assert.Equal(1, repository.ListGoalMetadataCount);
        AssertNoStateEffects(repository);
        return output;
    }

    private static string Execute(string[] args, OrchestratorWorkspace workspace, ProbeStateRepository repository)
    {
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var profiles = WorkerProfileCatalog.Default();
        Goal? current = null;
        return CaptureConsole(() =>
        {
            Assert.True(CliReadOnlyCommandRunner.TryExecute(args, repository, workspace,
                new InMemoryModelProviderRegistry([]), ref agents, ref profiles, ref current, out var changed));
            Assert.False(changed);
        });
    }

    private static void AssertNoStateEffects(ProbeStateRepository repository) => Assert.Equal(0,
        repository.FullLoadAttempts + repository.LoadGoalsCount + repository.LoadGoalCount +
        repository.SaveAttempts + repository.MergeSaveAttempts + repository.MutationAttempts +
        repository.ListOutboxMessagesCount + repository.OutboxClaimAttempts);

    public void Dispose() => Directory.Delete(root, recursive: true);
}
