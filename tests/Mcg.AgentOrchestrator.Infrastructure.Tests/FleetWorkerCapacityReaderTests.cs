using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;
using Xunit;

// Parallel-safe: isolated roots, explicit registry/bindings, no global state or live processes.
public sealed class FleetWorkerCapacityReaderTests
{
    internal static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-03T12:00:00Z", CultureInfo.InvariantCulture);

    [Fact]
    public async Task Projection_MatchesDriverPredicateAndTaskMultiplicity_WithoutHydratingGoals()
    {
        using var fixture = new Fixture();
        var running = new TaskProcessRecord(123, "fixture", "unused", "out", "err", "exit", Now, null, null);
        TaskProcessRecord[] processes = [running, running with { WasCancelled = true },
            running with { CompletedAt = Now }, running with { ExitCode = 0 },
            running with { CompletedAt = Now, ExitCode = 1 }, running with { WasCancelledByConductor = true }];
        fixture.Seed(fixture.Home, JsonSerializer.Serialize(new
        {
            // Intentionally invalid unrelated Goal fields make whole-kernel hydration unusable.
            Id = 123, Status = "Completed", Objective = new { Unexpected = true },
            Tasks = processes.Select((process, index) => new { LastProcess = process,
                Status = index % 2 == 0 ? "Completed" : "Failed", Subprocesses = new[] { running } })
                .Cast<object>().Append(new { Status = "Running", Subprocesses = new[] { running } })
        }));
        fixture.Insert(fixture.Home, "metadata", "{\"Tasks\":[],\"IsMetadataOnly\":true}");
        var statements = new List<string>();
        var before = fixture.Snapshot();
        var schema = fixture.Schema(fixture.Home);
        var count = await SqliteWorkerOccupancyReader.ReadAsync(fixture.Home.SqliteStatePath, statementObserver: statements.Add);
        Assert.Equal(processes.LongCount(process => process.IsRunning), count);
        Assert.Equal(3, count); // Shared PID is counted per task; cancellation flags are not termination.
        Assert.Contains(statements, sql => sql.Contains("json_each(goals.snapshot_json, '$.Tasks')", StringComparison.Ordinal));
        Assert.DoesNotContain(statements, sql => sql.Contains("SELECT id, snapshot_json", StringComparison.Ordinal) ||
            sql.Contains("goal_snapshot_archive", StringComparison.Ordinal));
        Assert.Equal(before, fixture.Snapshot());
        Assert.Equal(schema, fixture.Schema(fixture.Home));
    }

    [Fact]
    public async Task Catalog_DeduplicatesCanonicalAliases_PrefersHomeAndSumsDistinctLocalCaps()
    {
        using var fixture = new Fixture();
        var alpha = fixture.Register("alpha");
        var beta = fixture.Register("beta");
        fixture.Seed(fixture.Home, "{\"Tasks\":[{\"LastProcess\":{}}]}", 2);
        fixture.Seed(alpha, "{\"Tasks\":[{\"LastProcess\":{}},{\"LastProcess\":{}}]}", 7);
        fixture.Seed(beta, "{\"Tasks\":[]}", 3);
        AttentionWorkspaceEntry[] entries = [new("zulu", alpha), new("alpha", alpha), new("beta", beta),
            new("home-alias", fixture.Home), new("home", fixture.Home)];
        foreach (var ordered in new[] { entries, entries.Reverse().ToArray() })
        {
            var paths = new List<string>();
            var result = await FleetWorkerCapacityReader.ReadAsync(new(ordered), Now, readOccupancy: (path, token) =>
            {
                paths.Add(path);
                return SqliteWorkerOccupancyReader.ReadAsync(path, token);
            });
            Assert.Equal(new[] { "alpha", "beta", "home" }, result.Projects.Select(project => project.Project));
            Assert.Equal(3, paths.Distinct().Count());
            Assert.Equal(3, paths.Count);
            Assert.Equal(new[] { "zulu" }, result.Projects[0].Aliases);
            Assert.Equal(new[] { "home-alias" }, result.Projects[2].Aliases);
            Assert.Equal(3, result.KnownPersistedRunningWorkers);
            Assert.Equal(12, result.TheoreticalConfiguredProjectWorkerCapSum);
            Assert.True(result.Complete);
            Assert.True(result.CatalogComplete);
            Assert.Equal(0, result.UnavailableProjectCount);
        }
        Assert.Equal(alpha.RootDirectory, beta.RootDirectory);
        Assert.NotEqual(alpha.SqliteStatePath, beta.SqliteStatePath);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task IndependentMetricFailures_PreserveHealthyMetricsAndKnownPartialAggregates(bool occupancyGood, bool policyGood)
    {
        using var fixture = new Fixture();
        var bad = fixture.Register("bad");
        fixture.Seed(fixture.Home, "{\"Tasks\":[{\"LastProcess\":{}}]}", 2);
        fixture.Seed(bad, occupancyGood ? "{\"Tasks\":[{\"LastProcess\":{}},{\"LastProcess\":{}}]}" : "{broken", 9);
        if (!policyGood) File.WriteAllText(Path.Combine(bad.OrchestratorDirectory, "conductor-policy.json"), "{invalid");
        var before = fixture.Snapshot();
        var schema = fixture.Schema(bad);
        var result = await FleetWorkerCapacityReader.ReadAsync(fixture.Catalog(), Now);
        var row = Assert.Single(result.Projects, project => project.Project == "bad");
        Assert.Equal("unavailable", row.Availability);
        Assert.Equal(occupancyGood ? 2L : (long?)null, row.PersistedRunningWorkers);
        Assert.Equal(policyGood ? 9 : (int?)null, row.ConfiguredProjectWorkerCap);
        Assert.Equal(occupancyGood ? 3L : 1L, result.KnownPersistedRunningWorkers);
        Assert.Equal(policyGood ? 11L : 2L, result.TheoreticalConfiguredProjectWorkerCapSum);
        Assert.Equal(1, result.UnavailableProjectCount);
        Assert.False(result.Complete);
        if (!occupancyGood) Assert.Contains(row.Errors, error => error.Metric == "occupancy" && error.Code == "malformed-data");
        if (!policyGood) Assert.Contains(row.Errors, error => error.Metric == "policy" && error.Code == "invalid-policy");
        Assert.Equal(before, fixture.Snapshot());
        Assert.Equal(schema, fixture.Schema(bad));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"Tasks\":null}")]
    [InlineData("{\"Tasks\":{}}")]
    [InlineData("{\"Tasks\":[null]}")]
    [InlineData("{\"Tasks\":[\"oops\"]}")]
    [InlineData("{\"Tasks\":[{\"LastProcess\":true}]}")]
    [InlineData("{\"Tasks\":[{\"LastProcess\":{\"CompletedAt\":12}}]}")]
    [InlineData("{\"Tasks\":[{\"LastProcess\":{\"CompletedAt\":\"invalid\"}}]}")]
    [InlineData("{\"Tasks\":[{\"LastProcess\":{\"CompletedAt\":\"November 3, 2026\"}}]}")]
    [InlineData("{\"Tasks\":[{\"LastProcess\":{\"ExitCode\":\"0\"}}]}")]
    [InlineData("{\"Tasks\":[{\"LastProcess\":{\"ExitCode\":1.2}}]}")]
    [InlineData("{\"Tasks\":[{\"LastProcess\":{\"ExitCode\":2147483648}}]}")]
    [InlineData("{\"Tasks\":[],\"Tasks\":[{\"LastProcess\":{}}]}")]
    [InlineData("{\"Tasks\":[{\"LastProcess\":{},\"LastProcess\":null}]}")]
    [InlineData("{\"Tasks\":[{\"LastProcess\":{\"ExitCode\":null,\"ExitCode\":0}}]}")]
    public async Task MalformedRelevantData_IsUnknownNeverZero_AndUnchanged(string json)
    {
        using var fixture = new Fixture();
        fixture.Seed(fixture.Home, json);
        var before = fixture.Snapshot();
        var result = await FleetWorkerCapacityReader.ReadAsync(fixture.Catalog(), Now);
        Assert.False(result.Complete);
        var row = Assert.Single(result.Projects);
        Assert.Null(row.PersistedRunningWorkers);
        Assert.Equal("malformed-data", Assert.Single(row.Errors).Code);
        Assert.NotNull(row.ConfiguredProjectWorkerCap);
        Assert.Equal(before, fixture.Snapshot());
    }

    [Theory]
    [InlineData("missing", "missing-store")]
    [InlineData("unsupported", "unsupported-schema")]
    [InlineData("columns", "unsupported-schema")]
    [InlineData("unreadable", "unreadable-store")]
    public async Task StoreFailures_AreTypedAndNeverCreateOrRepairPaths(string failure, string expected)
    {
        using var fixture = new Fixture();
        if (failure != "missing") fixture.Seed(fixture.Home, "{\"Tasks\":[]}");
        if (failure == "unsupported") fixture.Sql(fixture.Home, "UPDATE meta SET value='future' WHERE key='schema_version'");
        if (failure == "columns") fixture.Sql(fixture.Home, "ALTER TABLE goals RENAME COLUMN snapshot_json TO unsupported_body");
        if (failure == "unreadable") File.WriteAllText(fixture.Home.SqliteStatePath, "not sqlite");
        var before = fixture.Snapshot();
        var result = await FleetWorkerCapacityReader.ReadAsync(fixture.Catalog(), Now);
        var row = Assert.Single(result.Projects);
        Assert.Null(row.PersistedRunningWorkers);
        Assert.Equal(expected, Assert.Single(row.Errors).Code);
        Assert.False(result.Complete);
        Assert.Equal(before, fixture.Snapshot());
        if (failure == "missing") Assert.False(Directory.Exists(fixture.Home.OrchestratorDirectory));
    }

    [Fact]
    public async Task EmptySupportedStore_IsHealthyZero_AndMissingPolicyUsesExistingDefault()
    {
        using var fixture = new Fixture();
        fixture.Seed(fixture.Home);
        var before = fixture.Snapshot();
        var result = await FleetWorkerCapacityReader.ReadAsync(fixture.Catalog(), Now);
        Assert.True(result.Complete);
        var row = Assert.Single(result.Projects);
        Assert.Equal(0L, row.PersistedRunningWorkers);
        Assert.Equal(ConductorAutonomyPolicy.Conservative.MaxConcurrentPaidWorkers, row.ConfiguredProjectWorkerCap);
        Assert.Equal(before, fixture.Snapshot());
        Assert.False(File.Exists(Path.Combine(fixture.Home.OrchestratorDirectory, "conductor-policy.json")));
    }

    [Fact]
    public async Task UnresolvedEntriesAndCatalogFailure_DoNotInventEquivalenceOrMissingProjectCounts()
    {
        using var fixture = new Fixture();
        fixture.Seed(fixture.Home, "{\"Tasks\":[{\"LastProcess\":{}}]}", 5);
        var result = await FleetWorkerCapacityReader.ReadAsync(new([
            new("home", fixture.Home), new("lost-a", null, new IOException()), new("lost-b", null, new IOException())],
            new IOException("untrusted\nregistry error")), Now);
        Assert.Equal(3, result.Projects.Count);
        Assert.Equal(2, result.UnavailableProjectCount);
        Assert.Equal(1, result.KnownPersistedRunningWorkers);
        Assert.Equal(5, result.TheoreticalConfiguredProjectWorkerCapSum);
        Assert.False(result.CatalogComplete);
        Assert.False(result.Complete);
        Assert.Equal("registry-unavailable", Assert.Single(result.Errors).Code);
        foreach (var row in result.Projects.Where(row => row.Project != "home"))
        {
            Assert.Empty(row.Aliases);
            Assert.Null(row.PersistedRunningWorkers);
            Assert.Null(row.ConfiguredProjectWorkerCap);
            Assert.Equal("project", Assert.Single(row.Errors).Metric);
            Assert.Equal("registry-entry-unavailable", row.Errors[0].Code);
        }
        var empty = await FleetWorkerCapacityReader.ReadAsync(new([], new IOException()), Now);
        Assert.Empty(empty.Projects);
        Assert.False(empty.Complete);
        Assert.False(empty.CatalogComplete);
        Assert.Equal(0, empty.KnownPersistedRunningWorkers);
        Assert.Equal(0, empty.TheoreticalConfiguredProjectWorkerCapSum);
        Assert.Equal(0, empty.UnavailableProjectCount);
        Assert.Single(empty.Errors);
    }

    [Fact]
    public async Task AggregatesUseLongAndFailLoudlyOnOverflow()
    {
        using var fixture = new Fixture();
        var other = fixture.Register("other");
        fixture.Seed(fixture.Home, cap: int.MaxValue);
        fixture.Seed(other, cap: int.MaxValue);
        var result = await FleetWorkerCapacityReader.ReadAsync(fixture.Catalog(), Now,
            readOccupancy: (_, _) => Task.FromResult((long)int.MaxValue));
        Assert.Equal(2L * int.MaxValue, result.KnownPersistedRunningWorkers);
        Assert.Equal(2L * int.MaxValue, result.TheoreticalConfiguredProjectWorkerCapSum);
        await Assert.ThrowsAsync<OverflowException>(() => FleetWorkerCapacityReader.ReadAsync(fixture.Catalog(), Now,
            readOccupancy: (_, _) => Task.FromResult(long.MaxValue)));
    }

    [Fact]
    public async Task CancellationDuringProjectionAndBetweenProjects_Propagates()
    {
        using var fixture = new Fixture();
        fixture.Seed(fixture.Home, "{\"Tasks\":[{\"LastProcess\":{}}]}");
        using var cancellation = new CancellationTokenSource();
        var observed = false;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SqliteWorkerOccupancyReader.ReadAsync(
            fixture.Home.SqliteStatePath, cancellation.Token, sql =>
            {
                if (!sql.Contains("FROM goals, json_each", StringComparison.Ordinal)) return;
                observed = true;
                cancellation.Cancel();
            }));
        Assert.True(observed);
        using var between = new CancellationTokenSource();
        var calls = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => FleetWorkerCapacityReader.ReadAsync(
            fixture.Catalog(), Now, between.Token, readOccupancy: (_, _) =>
            {
                calls++;
                between.Cancel();
                return Task.FromResult(1L);
            }));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Projection_UsesOneReadSnapshotAcrossValidationAndCounting()
    {
        using var fixture = new Fixture();
        fixture.Seed(fixture.Home, "{\"Tasks\":[{\"LastProcess\":{}}]}");
        var changed = false;
        var count = await SqliteWorkerOccupancyReader.ReadAsync(fixture.Home.SqliteStatePath, statementObserver: sql =>
        {
            if (!sql.Contains("FROM goals, json_each", StringComparison.Ordinal)) return;
            changed = true;
            fixture.Sql(fixture.Home, "UPDATE goals SET snapshot_json='{\"Tasks\":[]}'");
        });
        Assert.True(changed); // The competing write really committed between validation and counting.
        Assert.Equal(1, count);
        Assert.Equal(0, await SqliteWorkerOccupancyReader.ReadAsync(fixture.Home.SqliteStatePath));
    }

    [Fact]
    public async Task UnreadablePolicy_DoesNotSuppressSuccessfulOccupancy()
    {
        using var fixture = new Fixture();
        fixture.Seed(fixture.Home, "{\"Tasks\":[{\"LastProcess\":{}}]}");
        Directory.CreateDirectory(Path.Combine(fixture.Home.OrchestratorDirectory, "conductor-policy.json"));
        var before = fixture.Snapshot();
        var result = await FleetWorkerCapacityReader.ReadAsync(fixture.Catalog(), Now);
        var row = Assert.Single(result.Projects);
        Assert.Equal(1L, row.PersistedRunningWorkers);
        Assert.Null(row.ConfiguredProjectWorkerCap);
        Assert.Equal("invalid-policy", Assert.Single(row.Errors).Code);
        Assert.False(result.Complete);
        Assert.Equal(before, fixture.Snapshot());
    }

    internal sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Directory.CreateTempSubdirectory("fleet-capacity-").FullName;
        internal string Repository => Path.Combine(Root, "repository");
        internal string Data => Path.Combine(Root, "data");
        internal OrchestratorProjectRegistry Registry { get; }
        internal OrchestratorWorkspace Home => OrchestratorWorkspace.ForDirectory(Repository, null, null, Registry);
        internal Fixture()
        {
            Directory.CreateDirectory(Path.Combine(Repository, ".git"));
            Registry = new(Path.Combine(Root, "registry"), Data);
        }
        internal OrchestratorWorkspace Register(string name)
        {
            Registry.CreateProject(name, Repository);
            return OrchestratorWorkspace.ForProject(name, Repository, dataRootDirectory: Data);
        }
        internal AttentionWorkspaceCatalog Catalog() => OwnerConsoleBoardSource.ReadWorkspaceCatalog(Repository, null, Registry);
        internal void Seed(OrchestratorWorkspace workspace, string? json = null, int? cap = null)
        {
            StateDbMigrations.EnsureUpToDate(workspace.SqliteStatePath);
            if (json is not null) Insert(workspace, "fixture-goal", json);
            if (cap is not null) File.WriteAllText(Path.Combine(workspace.OrchestratorDirectory, "conductor-policy.json"),
                (ConductorAutonomyPolicy.Conservative with { MaxConcurrentPaidWorkers = cap.Value }).ToJson());
        }
        internal void Insert(OrchestratorWorkspace workspace, string id, string json) => Sql(workspace,
            "INSERT INTO goals (id,status,objective,updated_at,snapshot_json) VALUES ($id,'Completed','fixture','2026-11-03',$json)",
            ("$id", id), ("$json", json));
        internal void Sql(OrchestratorWorkspace workspace, string sql, params (string Key, string Value)[] values)
        {
            using var connection = Open(workspace);
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            foreach (var (key, value) in values) command.Parameters.AddWithValue(key, value);
            command.ExecuteNonQuery();
        }
        internal string Schema(OrchestratorWorkspace workspace)
        {
            using var connection = Open(workspace);
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT group_concat(sql, char(10)) FROM (SELECT sql FROM sqlite_master ORDER BY name)";
            return (string)command.ExecuteScalar()!;
        }
        private static SqliteConnection Open(OrchestratorWorkspace workspace)
        {
            var connection = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = workspace.SqliteStatePath, Pooling = false, Mode = SqliteOpenMode.ReadWrite }.ToString());
            connection.Open();
            return connection;
        }
        // WAL/SHM are SQLite read coordination files; assert durable store and registry bytes/schema.
        internal string[] Snapshot() => Directory.GetFiles(Root, "*", SearchOption.AllDirectories)
            .Where(path => !path.EndsWith("-wal", StringComparison.Ordinal) && !path.EndsWith("-shm", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal).Select(path => Path.GetRelativePath(Root, path) + ":" +
                Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))).ToArray();
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
