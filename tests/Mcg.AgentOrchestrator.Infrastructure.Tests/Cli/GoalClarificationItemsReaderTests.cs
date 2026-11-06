using System.Globalization;
using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;
using Xunit;

// Parallel-safe: each test owns its workspace, connections and retry clock.
public sealed class GoalClarificationItemsReaderTests
{
    private static readonly GoalId TargetId = new("abcdef12abcdef12abcdef12abcdef12");

    [Fact]
    public async Task HeldWriteLockPastShortenedBudgetDegradesWithOnePopulatedLine()
    {
        using var fixture = new StoreFixture();
        await fixture.SeedAsync();
        using var holder = HoldWriteLock(fixture.Path);
        var attempts = 0;
        var sleeps = 0;
        var elapsed = TimeSpan.Zero;
        var retry = new StartupSqliteRetryOptions(
            Sleep: _ => { sleeps++; elapsed = TimeSpan.FromSeconds(30); },
            Elapsed: () => elapsed,
            OnAttempt: (path, operation) =>
            {
                Assert.Equal(fixture.Path, path);
                Assert.Equal("status-read", operation);
                attempts++;
            });

        var result = GoalClarificationItemsReader.Read(fixture.Path, TargetId.Value, retry);

        Assert.Empty(result.Items);
        Assert.Equal(2, attempts);
        Assert.Equal(1, sleeps);
        var line = Assert.IsType<string>(result.DegradedRead);
        var match = Regex.Match(line,
            @"\Acollaboration-items unavailable store=(?<path>.+) operation=status-read attempts=(?<attempts>\d+) elapsed_ms=(?<elapsed>\d+)\z");
        Assert.True(match.Success, line);
        Assert.Equal(fixture.Path, match.Groups["path"].Value);
        Assert.Equal(attempts, int.Parse(match.Groups["attempts"].Value, CultureInfo.InvariantCulture));
        Assert.Equal(30000, int.Parse(match.Groups["elapsed"].Value, CultureInfo.InvariantCulture));
        Assert.DoesNotContain("unknown", line);
        Assert.DoesNotContain('\r', line);
        Assert.DoesNotContain('\n', line);
        Assert.DoesNotContain("stack:", line);
        Assert.DoesNotContain("   at ", line);
    }

    [Fact]
    public async Task LockReleasedAfterFirstAttemptReturnsItemsAndUnlockedStatusText()
    {
        using var fixture = new StoreFixture();
        var seeded = await fixture.SeedAsync();
        var unlocked = GoalClarificationItemsReader.Read(fixture.Path, TargetId.Value);
        Assert.Equal(seeded.Id, Assert.Single(unlocked.Items).Id);
        Assert.Null(unlocked.DegradedRead);
        var kernel = new AgentOrchestratorKernel();
        kernel.CreateGoal(TargetId, "Read clarification status",
            [new TaskSpec(TaskId.New(), "Pending implementation", AgentRole.Developer)]);
        kernel.ActivateGoal(TargetId, AgentCatalog.Default().Agents);
        var goal = kernel.GetGoal(TargetId);
        Assert.Equal(GoalStatus.Active, goal.Status);
        var unlockedStatus = CliCommandHandlers.ResolveGoalStatusText(fixture.Workspace, goal);
        Assert.Equal("Active", unlockedStatus);

        using (var holder = HoldWriteLock(fixture.Path))
        {
            var attempts = 0;
            var releases = 0;
            var retry = ReleaseAfterFirstFailure(holder, () => attempts++, () => releases++);
            var recovered = GoalClarificationItemsReader.Read(fixture.Path, TargetId.Value, retry);
            Assert.Equal(seeded.Id, Assert.Single(recovered.Items).Id);
            Assert.Null(recovered.DegradedRead);
            Assert.Equal(2, attempts);
            Assert.Equal(1, releases);
        }

        using (var holder = HoldWriteLock(fixture.Path))
        {
            var attempts = 0;
            var releases = 0;
            var retry = ReleaseAfterFirstFailure(holder, () => attempts++, () => releases++);
            var degradedReads = new List<string>();
            var recoveredStatus = CliCommandHandlers.ResolveGoalStatusText(
                fixture.Workspace, goal, degradedReads.Add, retry);
            Assert.Equal(unlockedStatus, recoveredStatus);
            Assert.Empty(degradedReads);
            Assert.Equal(2, attempts);
            Assert.Equal(1, releases);
        }
    }

    [Fact]
    public async Task SchemaRefusalPropagatesUnchangedWithoutDegradedLine()
    {
        using var fixture = new StoreFixture();
        await fixture.SeedAsync();
        using (var connection = OpenWriter(fixture.Path))
        {
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE store_schema_versions SET version = version + 1 WHERE store_name = $store";
            command.Parameters.AddWithValue("$store", "collaboration-items");
            Assert.Equal(1, command.ExecuteNonQuery());
        }
        var expected = Assert.Throws<InvalidOperationException>(
            () => CollaborationItemStore.OpenReadOnly(fixture.Path));
        var attempts = 0;
        var retry = new StartupSqliteRetryOptions(
            Sleep: _ => Assert.Fail("Schema refusal must not retry."),
            OnAttempt: (_, _) => attempts++);
        GoalClarificationItemsReader.Result? result = null;

        var actual = Assert.Throws<InvalidOperationException>(() =>
        {
            result = GoalClarificationItemsReader.Read(fixture.Path, TargetId.Value, retry);
        });

        Assert.Equal(expected.Message, actual.Message);
        Assert.Contains("schema is Newer", actual.Message);
        Assert.Null(actual.InnerException);
        Assert.False(actual.Data.Contains("Mcg.AttemptCount"));
        Assert.Null(result);
        Assert.Equal(1, attempts);
    }

    [Fact]
    public void MissingDatabaseReturnsEmptyWithoutDegradedLine()
    {
        using var fixture = new StoreFixture();
        var result = GoalClarificationItemsReader.Read(fixture.Path, TargetId.Value,
            new StartupSqliteRetryOptions(OnAttempt: (_, _) => Assert.Fail("Missing store must not be opened.")));

        Assert.Empty(result.Items);
        Assert.Null(result.DegradedRead);
        Assert.False(File.Exists(fixture.Path));
    }

    private static StartupSqliteRetryOptions ReleaseAfterFirstFailure(
        SqliteConnection holder, Action onAttempt, Action onRelease)
    {
        var elapsed = TimeSpan.Zero;
        return new StartupSqliteRetryOptions(
            Sleep: _ => { holder.Dispose(); onRelease(); elapsed = TimeSpan.FromSeconds(30); },
            Elapsed: () => elapsed,
            OnAttempt: (_, _) => onAttempt());
    }

    private static SqliteConnection HoldWriteLock(string path)
    {
        var holder = OpenWriter(path);
        try
        {
            using var command = holder.CreateCommand();
            // Initialize live WAL sidecars before locking, selecting the reader's
            // SQLite snapshot path rather than the quiet-file copy path.
            command.CommandText = "SELECT COUNT(*) FROM store_schema_versions";
            command.ExecuteScalar();
            Assert.True(File.Exists(path + "-wal"), "WAL must exist before the locked read.");
            Assert.True(File.Exists(path + "-shm"), "SHM must exist before the locked read.");
            command.CommandText = "PRAGMA locking_mode=EXCLUSIVE";
            Assert.Equal("exclusive", command.ExecuteScalar());
            command.CommandText = "BEGIN EXCLUSIVE";
            command.ExecuteNonQuery();
            command.CommandText = "UPDATE store_schema_versions SET version = version";
            Assert.True(command.ExecuteNonQuery() > 0, "The holder must acquire a write lock.");
            return holder;
        }
        catch { holder.Dispose(); throw; }
    }

    private static SqliteConnection OpenWriter(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, Mode = SqliteOpenMode.ReadWrite, Pooling = false
        }.ToString());
        try { connection.Open(); return connection; }
        catch { connection.Dispose(); throw; }
    }

    private sealed class StoreFixture : IDisposable
    {
        private readonly string _root = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), nameof(GoalClarificationItemsReaderTests), Guid.NewGuid().ToString("N"));
        internal OrchestratorWorkspace Workspace { get; }
        internal string Path { get; }

        internal StoreFixture()
        {
            Directory.CreateDirectory(_root);
            Workspace = OrchestratorWorkspace.ForDirectory(_root);
            Path = System.IO.Path.Combine(Workspace.OrchestratorDirectory, "collaboration-items.db");
        }

        internal Task<CollaborationItem> SeedAsync() => new CollaborationItemStore(Path).RaiseAsync(
            CollaborationItemType.Clarification, TargetId.Value, "Need input", "Question",
            SpecClarificationKey.Build(TargetId, "reader-test"), TestContext.Current.CancellationToken);

        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}
