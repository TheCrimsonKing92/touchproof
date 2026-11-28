using System.Diagnostics;
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
    private const int LockedReadBusyTimeoutMilliseconds = 50;
    private static readonly GoalId TargetId = new("abcdef12abcdef12abcdef12abcdef12");

    [Fact]
    public async Task HeldWriteLockPastShortenedBudgetDegradesWithOnePopulatedLine()
    {
        using var timing = new StepTimings();
        using var fixture = new StoreFixture();
        timing.Mark("fixture-setup");
        await fixture.SeedAsync();
        timing.Mark("seed");
        using var holder = HoldWriteLock(fixture.Path);
        timing.Mark("reader-lock-setup");
        var attempts = 0;
        var sleeps = 0;
        var elapsed = TimeSpan.Zero;
        var retry = new StartupSqliteRetryOptions(
            Sleep: _ =>
            {
                timing.Mark("reader-attempt-1-locked");
                sleeps++;
                elapsed = TimeSpan.FromSeconds(30);
            },
            Elapsed: () => elapsed,
            OnAttempt: (path, operation) =>
            {
                timing.Mark("reader-retry-overhead");
                Assert.Equal(fixture.Path, path);
                Assert.Equal("status-read", operation);
                attempts++;
                timing.Mark("reader-attempt-assertions");
            });

        var result = GoalClarificationItemsReader.Read(fixture.Path, TargetId.Value, retry,
            busyTimeoutMilliseconds: LockedReadBusyTimeoutMilliseconds);
        timing.Mark("reader-attempt-2-locked");

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
        timing.Mark("assertions");
    }

    [Fact]
    public async Task LockReleasedAfterFirstAttemptReturnsItemsAndUnlockedStatusText()
    {
        using var timing = new StepTimings();
        using var fixture = new StoreFixture();
        timing.Mark("fixture-setup");
        var seeded = await fixture.SeedAsync();
        timing.Mark("seed");
        var unlocked = GoalClarificationItemsReader.Read(fixture.Path, TargetId.Value);
        timing.Mark("reader-unlocked");
        Assert.Equal(seeded.Id, Assert.Single(unlocked.Items).Id);
        Assert.Null(unlocked.DegradedRead);
        timing.Mark("assertions");
        var kernel = new AgentOrchestratorKernel();
        kernel.CreateGoal(TargetId, "Read clarification status",
            [new TaskSpec(TaskId.New(), "Pending implementation", AgentRole.Developer)]);
        kernel.ActivateGoal(TargetId, AgentCatalog.Default().Agents);
        var goal = kernel.GetGoal(TargetId);
        timing.Mark("goal-setup");
        Assert.Equal(GoalStatus.Active, goal.Status);
        timing.Mark("assertions");
        var unlockedStatus = CliCommandHandlers.ResolveGoalStatusText(fixture.Workspace, goal);
        timing.Mark("resolver-unlocked");
        Assert.Equal("Active", unlockedStatus);
        timing.Mark("assertions");

        using (var holder = HoldWriteLock(fixture.Path))
        {
            timing.Mark("reader-lock-setup");
            var attempts = 0;
            var releases = 0;
            var retry = ReleaseAfterFirstFailure(holder, () => attempts++, () => releases++, timing, "reader");
            var recovered = GoalClarificationItemsReader.Read(fixture.Path, TargetId.Value, retry,
                busyTimeoutMilliseconds: LockedReadBusyTimeoutMilliseconds);
            timing.Mark("reader-attempt-2-unlocked");
            Assert.Equal(seeded.Id, Assert.Single(recovered.Items).Id);
            Assert.Null(recovered.DegradedRead);
            Assert.Equal(2, attempts);
            Assert.Equal(1, releases);
            timing.Mark("assertions");
        }

        using (var holder = HoldWriteLock(fixture.Path))
        {
            timing.Mark("resolver-lock-setup");
            var attempts = 0;
            var releases = 0;
            var retry = ReleaseAfterFirstFailure(holder, () => attempts++, () => releases++, timing, "resolver");
            var degradedReads = new List<string>();
            var recoveredStatus = CliCommandHandlers.ResolveGoalStatusText(
                fixture.Workspace, goal, degradedReads.Add, retry,
                busyTimeoutMilliseconds: LockedReadBusyTimeoutMilliseconds);
            timing.Mark("resolver-attempt-2-unlocked");
            Assert.Equal(unlockedStatus, recoveredStatus);
            Assert.Empty(degradedReads);
            Assert.Equal(2, attempts);
            Assert.Equal(1, releases);
            timing.Mark("assertions");
        }
    }

    [Fact]
    public async Task SchemaRefusalPropagatesUnchangedWithoutDegradedLine()
    {
        using var timing = new StepTimings();
        using var fixture = new StoreFixture();
        timing.Mark("fixture-setup");
        await fixture.SeedAsync();
        timing.Mark("seed");
        using (var connection = OpenWriter(fixture.Path))
        {
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE store_schema_versions SET version = version + 1 WHERE store_name = $store";
            command.Parameters.AddWithValue("$store", "collaboration-items");
            timing.Mark("schema-update-setup");
            Assert.Equal(1, command.ExecuteNonQuery());
            timing.Mark("schema-update-and-assertion");
        }
        var expected = Assert.Throws<InvalidOperationException>(
            () => CollaborationItemStore.OpenReadOnly(fixture.Path));
        timing.Mark("store-schema-refusal-and-assertion");
        var attempts = 0;
        var retry = new StartupSqliteRetryOptions(
            Sleep: _ => Assert.Fail("Schema refusal must not retry."),
            OnAttempt: (_, _) => attempts++);
        GoalClarificationItemsReader.Result? result = null;

        var actual = Assert.Throws<InvalidOperationException>(() =>
        {
            result = GoalClarificationItemsReader.Read(fixture.Path, TargetId.Value, retry);
        });
        timing.Mark("reader-schema-refusal-and-assertion");

        Assert.Equal(expected.Message, actual.Message);
        Assert.Contains("schema is Newer", actual.Message);
        Assert.Null(actual.InnerException);
        Assert.False(actual.Data.Contains("Mcg.AttemptCount"));
        Assert.Null(result);
        Assert.Equal(1, attempts);
        timing.Mark("assertions");
    }

    [Fact]
    public void MissingDatabaseReturnsEmptyWithoutDegradedLine()
    {
        using var timing = new StepTimings();
        using var fixture = new StoreFixture();
        timing.Mark("fixture-setup");
        var result = GoalClarificationItemsReader.Read(fixture.Path, TargetId.Value,
            new StartupSqliteRetryOptions(OnAttempt: (_, _) => Assert.Fail("Missing store must not be opened.")));
        timing.Mark("reader-missing-store");

        Assert.Empty(result.Items);
        Assert.Null(result.DegradedRead);
        Assert.False(File.Exists(fixture.Path));
        timing.Mark("assertions");
    }

    [Fact]
    public async Task ExplicitBusyTimeoutConfiguresSnapshotConnection()
    {
        var settings = await ReadSnapshotTimeoutsAsync(path => GoalClarificationItemsReader.Read(path, TargetId.Value,
            busyTimeoutMilliseconds: LockedReadBusyTimeoutMilliseconds));

        Assert.Equal((50L, 1), settings);
    }

    [Fact]
    public async Task OmittedBusyTimeoutUsesThirtySecondSnapshotDefault()
    {
        var settings = await ReadSnapshotTimeoutsAsync(path => GoalClarificationItemsReader.Read(path, TargetId.Value));

        Assert.Equal((30_000L, 30), settings);
    }

    private static async Task<(long BusyTimeoutMilliseconds, int DefaultTimeoutSeconds)> ReadSnapshotTimeoutsAsync(
        Action<string> read)
    {
        using var fixture = new StoreFixture();
        await fixture.SeedAsync();
        using var writer = OpenWriter(fixture.Path);
        using var initializeWal = writer.CreateCommand();
        initializeWal.CommandText = "SELECT COUNT(*) FROM store_schema_versions";
        initializeWal.ExecuteScalar();
        Assert.True(File.Exists(fixture.Path + "-wal"));
        Assert.True(File.Exists(fixture.Path + "-shm"));

        var observed = new List<(long, int)>();
        StoreInspectionSnapshot.ConnectionOpenedForDiagnostics += Observe;
        try
        {
            read(fixture.Path);
            return Assert.Single(observed);
        }
        finally
        {
            StoreInspectionSnapshot.ConnectionOpenedForDiagnostics -= Observe;
        }

        void Observe(SqliteConnection connection, string source)
        {
            if (source != fixture.Path)
                return;

            Assert.Equal(fixture.Path, System.IO.Path.GetFullPath(connection.DataSource),
                ignoreCase: OperatingSystem.IsWindows());
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA busy_timeout";
            observed.Add((Assert.IsType<long>(command.ExecuteScalar()), connection.DefaultTimeout));
        }
    }

    private static StartupSqliteRetryOptions ReleaseAfterFirstFailure(
        SqliteConnection holder, Action onAttempt, Action onRelease, StepTimings timing, string operation)
    {
        var elapsed = TimeSpan.Zero;
        return new StartupSqliteRetryOptions(
            Sleep: _ =>
            {
                timing.Mark($"{operation}-attempt-1-locked");
                holder.Dispose();
                onRelease();
                elapsed = TimeSpan.FromSeconds(30);
                timing.Mark($"{operation}-lock-release");
            },
            Elapsed: () => elapsed,
            OnAttempt: (_, _) =>
            {
                onAttempt();
                timing.Mark($"{operation}-retry-overhead");
            });
    }

    private sealed class StepTimings : IDisposable
    {
        private readonly List<(string Step, TimeSpan Duration)> _steps = [];
        private readonly long _started = Stopwatch.GetTimestamp();
        private long _previous;

        internal StepTimings() => _previous = _started;

        internal void Mark(string step)
        {
            var now = Stopwatch.GetTimestamp();
            _steps.Add((step, Stopwatch.GetElapsedTime(_previous, now)));
            _previous = now;
        }

        public void Dispose()
        {
            Mark("cleanup");
            var output = TestContext.Current.TestOutputHelper;
            foreach (var (step, duration) in _steps)
                output?.WriteLine(FormattableString.Invariant($"step={step} elapsed_ms={duration.TotalMilliseconds:F3}"));
            output?.WriteLine(FormattableString.Invariant(
                $"step=total elapsed_ms={Stopwatch.GetElapsedTime(_started, _previous).TotalMilliseconds:F3}"));
        }
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
