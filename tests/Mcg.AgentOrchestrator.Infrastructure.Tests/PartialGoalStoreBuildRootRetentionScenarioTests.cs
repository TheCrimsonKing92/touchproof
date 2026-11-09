using System.Globalization;
using System.Security.Cryptography;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

// Parallel-safe: each instance owns its SQLite stores, registry, repository and storage root.
public sealed class PartialGoalStoreBuildRootRetentionScenarioTests : IDisposable
{
    private readonly string _temp = Directory.CreateTempSubdirectory("partial-retention-").FullName;
    private readonly DateTimeOffset _now = DateTimeOffset.UtcNow.AddDays(30);
    private readonly OrchestratorProjectRegistry _projects;
    private readonly TerminalGoalSweep.OwnedRootSweepState _state = new();
    private string Repo => Path.Combine(_temp, "repo");
    private string Db => Path.Combine(Repo, ".orchestrator", "state.db");
    private DotnetBuildStorageRoot Storage => new(Path.Combine(_temp, "isolated"));

    public PartialGoalStoreBuildRootRetentionScenarioTests()
    {
        _projects = new OrchestratorProjectRegistry(Path.Combine(_temp, "registry"), Path.Combine(_temp, "data"));
        StateDbMigrations.EnsureUpToDate(Db);
    }

    [Fact]
    public void SchemaBehind_ReclaimsReadableTerminalAndDefersUnstoredUntilRepair()
    {
        var projectDb = CreateProjectStore("price-selector");
        var terminal = CreateStoredRoot(Db, "aaaa0001", "Completed");
        var hidden = CreateStoredRoot(projectDb, "aaaa0002", "Active");
        var unstored = CreateLeaseless("aaaa0003");
        var hiddenBefore = Snapshot(hidden);
        var unstoredBefore = Snapshot(unstored);
        _state.UnstoredRetentionCursor = "12345678";
        Execute(projectDb, "CREATE TABLE saved_migration AS SELECT * FROM schema_migrations WHERE migration_number = (SELECT MAX(migration_number) FROM schema_migrations)");
        Execute(projectDb, "DELETE FROM schema_migrations WHERE migration_number = (SELECT migration_number FROM saved_migration)");
        Assert.True(StateDbMigrations.IsUpToDate(Db));
        Assert.False(StateDbMigrations.IsUpToDate(projectDb));
        Assert.Equal(2, TerminalGoalSweep.GetSharedGoalStorePaths(Db, out _, Repo, _projects)!.Count);

        var partial = Sweep();

        Assert.False(Directory.Exists(terminal));
        Assert.Equal(hiddenBefore, Snapshot(hidden));
        Assert.Equal(unstoredBefore, Snapshot(unstored));
        Assert.Equal("12345678", _state.UnstoredRetentionCursor);
        var summary = StoredSummary(partial);
        Assert.Contains("reclaimed=1", summary);
        Assert.Contains("unavailable-stores=price-selector:schema-behind", summary);
        Assert.Contains("unstored-skipped=goal-store-unavailable", summary);
        Assert.DoesNotContain("reasons=goal-store-unavailable", summary);
        AssertNoUnstored(partial);

        Execute(projectDb, "INSERT INTO schema_migrations SELECT * FROM saved_migration");
        Assert.True(StateDbMigrations.IsUpToDate(projectDb));
        var repaired = Sweep();

        Assert.False(Directory.Exists(unstored));
        Assert.Equal(hiddenBefore, Snapshot(hidden));
        Assert.Contains("reclaimed=1", UnstoredSummary(repaired));
        Assert.Equal("aaaa0003", _state.UnstoredRetentionCursor);
        Assert.DoesNotContain("unavailable-stores=", StoredSummary(repaired));
        Assert.DoesNotContain("unstored-skipped=", StoredSummary(repaired));
    }

    [Fact]
    public void AllReadable_PreservesExactSummariesAndReclaimDecisions()
    {
        var projectDb = CreateProjectStore("other");
        var terminal = CreateStoredRoot(Db, "aaaa0001", "Completed");
        var active = CreateStoredRoot(projectDb, "aaaa0002", "Active");
        var unstored = CreateLeaseless("aaaa0003");
        var activeBefore = Snapshot(active);
        var terminalBytes = Size(terminal);
        var unstoredBytes = Size(unstored);
        Assert.True(StateDbMigrations.IsUpToDate(Db));
        Assert.True(StateDbMigrations.IsUpToDate(projectDb));

        var result = Sweep();

        Assert.False(Directory.Exists(terminal));
        Assert.False(Directory.Exists(unstored));
        Assert.Equal(activeBefore, Snapshot(active));
        Assert.Equal(FormattableString.Invariant(
            $"SWEEP_BUILD_ROOT_RETENTION window-days=7 reclaimed=1 bytes={terminalBytes} skipped=2 reasons=goal-not-stored:1,goal-not-terminal:1"), StoredSummary(result));
        Assert.Equal(FormattableString.Invariant(
            $"SWEEP_UNSTORED_BUILD_ROOT_RETENTION window-days=7 inspected=1 reclaimed=1 bytes={unstoredBytes} kept=1 reasons=goal-stored:1"), UnstoredSummary(result));
        var events = result.OperatorEvents.ToList();
        Assert.Equal(events.IndexOf(StoredSummary(result)) + 1, events.IndexOf(UnstoredSummary(result)));
        Assert.Equal("aaaa0003", _state.UnstoredRetentionCursor);
    }

    [Fact]
    public void InvalidRow_DiscardsEarlierFactsAndContinuesToLaterReadableStores()
    {
        var failedRoot = CreateStoredRoot(Db, "aaaa0001", "Completed");
        InsertGoal(Db, "ffff0001000000000000000000000000", "unknown-status");
        // Pin that the real SELECT reaches a valid row before the failing row.
        using (var connection = Open(Db))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT id, status, updated_at FROM goals";
            using var reader = command.ExecuteReader();
            Assert.True(reader.Read());
            Assert.Equal("Completed", reader.GetString(1));
            Assert.True(reader.Read());
            Assert.Equal("unknown-status", reader.GetString(1));
        }
        var readableDb = CreateProjectStore("other");
        var readableRoot = CreateStoredRoot(readableDb, "aaaa0002", "Completed");
        var before = Snapshot(failedRoot);
        _state.UnstoredRetentionCursor = "12345678";

        var result = Sweep();

        Assert.Equal(before, Snapshot(failedRoot));
        Assert.False(Directory.Exists(readableRoot));
        Assert.Contains("reclaimed=1", StoredSummary(result));
        Assert.Contains("unavailable-stores=default:read-failed", StoredSummary(result));
        Assert.DoesNotContain("reasons=goal-store-unavailable", StoredSummary(result));
        Assert.Equal("12345678", _state.UnstoredRetentionCursor);
        AssertNoUnstored(result);
    }

    [Fact]
    public void NoneReadable_ReportsEveryFailureInProjectOrderAndKeepsRoots()
    {
        var root = CreateStoredRoot(Db, "aaaa0001", "unknown-status");
        var zeta = CreateProjectStore("zeta");
        InsertGoal(zeta, "ffff0001000000000000000000000000", "unknown-status");
        var alpha = CreateProjectStore("alpha");
        InsertGoal(alpha, "ffff0002000000000000000000000000", "unknown-status");
        var before = Snapshot(root);
        _state.UnstoredRetentionCursor = "12345678";

        var result = Sweep();

        Assert.Equal("SWEEP_BUILD_ROOT_RETENTION window-days=7 reclaimed=0 bytes=0 skipped=0 reasons=goal-store-unavailable unavailable-stores=alpha:read-failed,default:read-failed,zeta:read-failed unstored-skipped=goal-store-unavailable", StoredSummary(result));
        Assert.Equal(before, Snapshot(root));
        Assert.Equal("12345678", _state.UnstoredRetentionCursor);
        AssertNoUnstored(result);
    }

    [Fact]
    public void EmptyReadableStore_AllowsTerminalPassWithoutGlobalUnavailableReason()
    {
        var failed = CreateProjectStore("price-selector");
        var root = CreateStoredRoot(failed, "aaaa0001", "unknown-status");
        var before = Snapshot(root);
        Assert.True(StateDbMigrations.IsUpToDate(Db));

        var result = Sweep();

        Assert.Equal("SWEEP_BUILD_ROOT_RETENTION window-days=7 reclaimed=0 bytes=0 skipped=1 reasons=goal-not-stored:1 unavailable-stores=price-selector:read-failed unstored-skipped=goal-store-unavailable", StoredSummary(result));
        Assert.Equal(before, Snapshot(root));
        AssertNoUnstored(result);
    }

    private TerminalGoalSweepOwnedRootResult Sweep() => TerminalGoalSweep.ReapOwnedBuildRootsCore(
        Db, Storage, _state, _ => false, usesSharedStorageRoot: true,
        canonicalRepoRoot: Repo, projectRegistry: _projects, reclaimGoalRoots: false,
        policy: ConductorAutonomyPolicy.Conservative with { BuildRootRetentionDays = 7 },
        timeProvider: new FixedTimeProvider(_now));

    private string CreateProjectStore(string name)
    {
        var root = Directory.CreateDirectory(Path.Combine(_temp, name)).FullName;
        _projects.CreateProject(name, root);
        var db = _projects.GetRequiredProject(name).ResolveWorkspace().SqliteStatePath;
        StateDbMigrations.EnsureUpToDate(db);
        return db;
    }

    private string CreateStoredRoot(string db, string prefix, string status)
    {
        var goal = new GoalId(prefix + new string('0', 24));
        InsertGoal(db, goal.Value, status);
        return DotnetBuildEnvironmentManager.CreateAttempt(goal, "acceptance",
            storageRoot: Storage, repositoryRoot: Repo).RootPath;
    }

    private string CreateLeaseless(string prefix)
    {
        var root = Directory.CreateDirectory(Path.Combine(Storage.RootPath, "goals", prefix)).FullName;
        File.WriteAllBytes(Path.Combine(root, "payload.bin"), new byte[123]);
        return root;
    }

    private void InsertGoal(string db, string id, string status)
    {
        using var connection = Open(db);
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO goals (id, status, objective, updated_at, snapshot_json) VALUES ($id, $status, 'partial retention fixture', $updated, '{}')";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$updated", _now.AddDays(-30).ToString("O", CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();
    }

    private static SqliteConnection Open(string db)
    {
        var connection = new SqliteConnection($"Data Source={db};Pooling=False");
        connection.Open();
        return connection;
    }

    private static void Execute(string db, string sql)
    {
        using var connection = Open(db);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static string StoredSummary(TerminalGoalSweepOwnedRootResult result) =>
        Assert.Single(result.OperatorEvents.Where(line => line.StartsWith("SWEEP_BUILD_ROOT_RETENTION ", StringComparison.Ordinal)));

    private static string UnstoredSummary(TerminalGoalSweepOwnedRootResult result) =>
        Assert.Single(result.OperatorEvents.Where(line => line.StartsWith("SWEEP_UNSTORED_BUILD_ROOT_RETENTION ", StringComparison.Ordinal)));

    private static void AssertNoUnstored(TerminalGoalSweepOwnedRootResult result) =>
        Assert.DoesNotContain(result.OperatorEvents, line => line.StartsWith("SWEEP_UNSTORED_BUILD_ROOT_RETENTION", StringComparison.Ordinal));

    private static long Size(string root) => Directory.GetFiles(root, "*", SearchOption.AllDirectories)
        .Sum(path => new FileInfo(path).Length);

    private static string[] Snapshot(string root) => Directory.GetFiles(root, "*", SearchOption.AllDirectories)
        .OrderBy(path => path, StringComparer.Ordinal)
        .Select(path => Path.GetRelativePath(root, path) + ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))).ToArray();

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    public void Dispose()
    {
        while (DotnetBuildEnvironmentManager.DrainOwnedRunRootWriteFailures(25, Storage).Count == 25) { }
        Directory.Delete(_temp, recursive: true);
    }
}
