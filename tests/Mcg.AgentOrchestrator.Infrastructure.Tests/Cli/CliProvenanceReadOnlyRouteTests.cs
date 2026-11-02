using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;
using Xunit;

// Parallel-safe: cases own their git workspace, SQLite store and child; console capture is async-local.
public sealed class CliProvenanceReadOnlyRouteTests : CliTaskQueryTestSupport
{
    private const string CompletedId = "abc10000aaaaaaaaaaaaaaaaaaaaaaaa";
    private static readonly DateTimeOffset FixtureTime = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("provenance")]
    [InlineData("PROVENANCE")]
    public void SingleArgument_ClassifiesAndExecutesAsQueryOnly(string verb)
    {
        WithWorkspace(workspace =>
        {
            Assert.Equal(CliCommandCapability.QueryOnly, CliCommandCapabilities.Classify([verb]));
            Assert.True(CliReadOnlyCommandRunner.IsReadOnlyCommand([verb]));
            var output = ExecuteQuery([verb], new QueryOnlyStateRepository(new AgentOrchestratorKernel()), workspace);
            Assert.Contains("No completed goals found.", output);
        });
    }

    [Theory]
    [InlineData("extra")]
    [InlineData("--help")]
    public void AdditionalArgument_DeclinesWithoutReadingRepository(string argument)
    {
        WithWorkspace(workspace =>
        {
            var repository = new ProbeStateRepository(new AgentOrchestratorKernel()) { ThrowOnOutbox = true };
            IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
            var profiles = WorkerProfileCatalog.Default();
            Goal? currentGoal = null;
            Assert.False(CliReadOnlyCommandRunner.IsReadOnlyCommand(["provenance", argument]));
            Assert.False(CliReadOnlyCommandRunner.TryExecute(["provenance", argument], repository, workspace,
                new InMemoryModelProviderRegistry([]), null, ref agents, ref profiles, ref currentGoal, out var changed));
            Assert.False(changed);
            Assert.Equal(0, repository.ListGoalMetadataCount);
            Assert.Equal(0, repository.LoadGoalsCount);
            Assert.Equal(0, repository.FullLoadAttempts);
        });
    }

    [Fact]
    public void BackedGoalWithLoggedSha_MatchesPinnedWriterOutput()
    {
        WithWorkspace(workspace =>
        {
            var sha = CreateGitRepository(workspace.RootDirectory);
            Assert.Equal(40, sha.Length);
            File.WriteAllText(Path.Combine(workspace.RootDirectory, "DOGFOOD_LOG.md"),
                $"goal abc10000 committed `{sha}`");
            var kernel = Kernel(CompletedSnapshot(CompletedId, "Backed provenance goal"));

            // Baseline from the former writer method's final dispatcher call and unchanged text view.
            var expected = string.Join(Environment.NewLine,
                "", "Provenance audit (1 completed goal(s)):", "  Backed:   1", "  Unbacked: 0", "",
                "  [BACKED  ] abc10000: Backed provenance goal", "",
                "  No unbacked dogfood references found.", "", "  No unbacked commit SHAs found.", "", "");
            IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
            var profiles = WorkerProfileCatalog.Default();
            Goal? currentGoal = null;
            var previousOutput = CaptureConsole(() => Assert.False(CliCommandDispatcher.ExecuteCommand(
                ["provenance"], kernel, workspace, ref agents, new InMemoryModelProviderRegistry([]),
                ref profiles, ref currentGoal)));
            Assert.Equal(expected, previousOutput);
            Assert.Equal(expected, ExecuteQuery(["provenance"], new QueryOnlyStateRepository(kernel), workspace));
        });
    }

    [Fact]
    public void UnbackedCompletedGoal_PreservesFailureException()
    {
        WithWorkspace(workspace =>
        {
            var kernel = Kernel(CompletedSnapshot(CompletedId, "Unbacked provenance goal", backed: false));
            IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
            var profiles = WorkerProfileCatalog.Default();
            Goal? currentGoal = null;
            var output = CaptureConsole(() =>
            {
                var error = Assert.Throws<InvalidOperationException>(() =>
                    CliReadOnlyCommandRunner.TryExecute(["provenance"], new QueryOnlyStateRepository(kernel), workspace,
                        new InMemoryModelProviderRegistry([]), null, ref agents, ref profiles, ref currentGoal, out _));
                Assert.Equal("Provenance check failed: 1 completed goal(s) are unbacked (missing verification receipts).",
                    error.Message);
            });
            Assert.Contains("[UNBACKED] abc10000", output);
        });
    }

    [Fact]
    public void MissingLoggedSha_PreservesFailureException()
    {
        WithWorkspace(workspace =>
        {
            CreateGitRepository(workspace.RootDirectory);
            File.WriteAllText(Path.Combine(workspace.RootDirectory, "DOGFOOD_LOG.md"),
                "committed `ffffffffffffffffffffffffffffffffffffffff`");
            var kernel = Kernel(CompletedSnapshot(CompletedId, "Backed provenance goal"));
            var error = Assert.Throws<InvalidOperationException>(() =>
                ExecuteQuery(["provenance"], new QueryOnlyStateRepository(kernel), workspace));
            Assert.Equal("Provenance check failed: 1 commit SHA(s) referenced in DOGFOOD_LOG.md are absent from git history.",
                error.Message);
        });
    }

    [Fact]
    public void MixedStatuses_LoadsOnlyCompletedGoalsInMetadataOrder()
    {
        WithWorkspace(workspace =>
        {
            const string newerId = "bbb10000bbbbbbbbbbbbbbbbbbbbbbbb";
            var template = CompletedSnapshot(CompletedId, "Older completed goal");
            var kernel = Kernel(template,
                CompletedSnapshot(newerId, "Newer completed goal", at: FixtureTime.AddMinutes(1)),
                template with { Id = "ccc10000cccccccccccccccccccccccc", Status = GoalStatus.Active },
                template with { Id = "ddd10000dddddddddddddddddddddddd", Status = GoalStatus.Draft },
                template with { Id = "eee10000eeeeeeeeeeeeeeeeeeeeeeee", Status = GoalStatus.Cancelled });
            var repository = new ProbeStateRepository(kernel) { ThrowOnOutbox = true };
            IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
            var profiles = WorkerProfileCatalog.Default();
            Goal? currentGoal = kernel.Goals.Single(goal => goal.Status == GoalStatus.Active);
            var callerGoal = currentGoal;
            var output = CaptureConsole(() =>
            {
                Assert.True(CliReadOnlyCommandRunner.TryExecute(["provenance"], repository, workspace,
                    new InMemoryModelProviderRegistry([]), null, ref agents, ref profiles, ref currentGoal, out var changed));
                Assert.False(changed);
            });

            Assert.Equal(new[] { newerId, CompletedId }, repository.RequestedGoalIds);
            Assert.Contains("[BACKED  ] bbb10000", output);
            Assert.Contains("[BACKED  ] abc10000", output);
            Assert.True(output.IndexOf("[BACKED  ] bbb10000", StringComparison.Ordinal) <
                output.IndexOf("[BACKED  ] abc10000", StringComparison.Ordinal));
            Assert.Contains("Provenance audit (2 completed goal(s)):", output);
            Assert.Same(callerGoal, currentGoal);
            Assert.Equal(1, repository.ListGoalMetadataCount);
            Assert.Equal(1, repository.LoadGoalsCount);
            Assert.Equal(0, repository.LoadGoalCount);
            Assert.Equal(0, repository.FullLoadAttempts);
            Assert.Equal(0, repository.BoardSnapshotLoads);
            Assert.Equal(0, repository.MutationAttempts);
            Assert.Equal(0, repository.SaveAttempts);
            Assert.Equal(0, repository.ListOutboxMessagesCount);
            Assert.Equal(0, repository.OutboxClaimAttempts);
            Assert.NotEmpty(repository.ObservedWriteOperationTags);
            Assert.All(repository.ObservedWriteOperationTags, tag => Assert.Null(tag));
            Assert.Null(SqliteOrchestratorStateRepository.AmbientWriteOperationTag);
        });
    }

    [Fact]
    public void CompletedSetBeyondBatchLimit_LoadsEveryGoal()
    {
        WithWorkspace(workspace =>
        {
            var template = CompletedSnapshot(CompletedId, "Batched goal");
            var goals = Enumerable.Range(1, 501).Select(index => template with
            {
                Id = index.ToString("x8", CultureInfo.InvariantCulture) + new string('a', 24)
            }).ToArray();
            var repository = new ProbeStateRepository(Kernel(goals)) { ThrowOnOutbox = true };
            var output = ExecuteQuery(["provenance"], repository, workspace);
            Assert.Equal(goals.Select(goal => goal.Id), repository.RequestedGoalIds);
            Assert.Equal(2, repository.LoadGoalsCount);
            Assert.Contains("Provenance audit (501 completed goal(s)):", output);
            Assert.Contains("[BACKED  ] 000001f5", output);
        });
    }

    [Fact]
    public void DisappearedCompletedGoal_IsSkipped()
    {
        WithWorkspace(workspace =>
        {
            var repository = new ProbeStateRepository(Kernel(CompletedSnapshot(CompletedId, "Gone goal")))
                { ThrowOnOutbox = true, DisappearSelectedGoals = true };
            var output = ExecuteQuery(["provenance"], repository, workspace);
            Assert.Equal(new[] { CompletedId }, repository.RequestedGoalIds);
            Assert.Contains("No completed goals found.", output);
        });
    }

    [Fact]
    public async Task HeldWriter_CompletesWithoutChangingGoalsOrOutbox()
    {
        var root = CreateTempDirectory();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(root, executionDirectory: root);
            StateDbMigrations.EnsureUpToDate(workspace.SqliteStatePath);
            var repository = new SqliteOrchestratorStateRepository(workspace.SqliteStatePath);
            await repository.SaveAsync(Kernel(CompletedSnapshot(CompletedId, "Held writer provenance")));
            // Exercise archived Completed goals as well as the held SQLite writer boundary.
            Assert.Equal(1, await repository.ArchiveTerminalGoalSnapshotsAsync(FixtureTime.AddDays(2)));
            _ = CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory);
            await using var writer = OpenConnection(workspace.SqliteStatePath);
            await writer.OpenAsync();
            await using (var insert = writer.CreateCommand())
            {
                insert.CommandText = "INSERT INTO state_outbox (id, kind, payload_json, created_at) VALUES ('held-provenance-outbox', $kind, '{}', $created)";
                insert.Parameters.AddWithValue("$kind", GoalOperationJournal.AcceptanceRetryAuditOutboxKind);
                insert.Parameters.AddWithValue("$created", FixtureTime.ToString("O"));
                Assert.Equal(1, await insert.ExecuteNonQueryAsync());
            }
            var goalsBefore = await ReadRowsAsync(writer, "SELECT * FROM goals ORDER BY id");
            var outboxBefore = await ReadRowsAsync(writer, "SELECT * FROM state_outbox ORDER BY id");
            await using (var begin = writer.CreateCommand())
            {
                begin.CommandText = "BEGIN IMMEDIATE";
                await begin.ExecuteNonQueryAsync();
            }
            try
            {
                Assert.True(CliReadOnlyCommandRunner.IsReadOnlyCommand(["provenance"]));
                var result = await RunQueryAsync(root);
                Assert.Equal(0, result.ExitCode);
                Assert.Equal(string.Empty, result.StandardError);
                Assert.Contains("[BACKED  ] abc10000: Held writer provenance", result.StandardOutput);
                await using var reader = OpenConnection(workspace.SqliteStatePath);
                await reader.OpenAsync();
                Assert.Equal(goalsBefore, await ReadRowsAsync(reader, "SELECT * FROM goals ORDER BY id"));
                Assert.Equal(outboxBefore, await ReadRowsAsync(reader, "SELECT * FROM state_outbox ORDER BY id"));
            }
            finally
            {
                await using var rollback = writer.CreateCommand();
                rollback.CommandText = "ROLLBACK";
                await rollback.ExecuteNonQueryAsync();
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static GoalSnapshot CompletedSnapshot(string id, string objective, bool backed = true,
        DateTimeOffset? at = null)
    {
        var clock = new FixedClock(at ?? FixtureTime);
        var kernel = new AgentOrchestratorKernel(clock);
        var task = new TaskSpec(TaskId.New(), "Completed implementation", AgentRole.Developer);
        var goal = kernel.CreateGoal(new GoalId(id), objective, [task]);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        if (backed)
            kernel.RecordTaskVerification(goal.Id, task.Id,
                new TaskVerificationRecord("fixture-verify", "fixture", 0, "verified", "", clock.UtcNow));
        var snapshot = kernel.ExportGoalSnapshot(goal.Id);
        return snapshot with
        {
            Status = GoalStatus.Completed,
            Tasks = snapshot.Tasks.Select(item => item with { Status = WorkTaskStatus.Completed }).ToArray()
        };
    }

    private static AgentOrchestratorKernel Kernel(params GoalSnapshot[] goals) =>
        AgentOrchestratorKernel.FromSnapshot(new OrchestratorSnapshot(goals, []));

    private static string ExecuteQuery(string[] args, IOrchestratorStateQueries repository,
        OrchestratorWorkspace workspace)
    {
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;
        return CaptureConsole(() =>
        {
            Assert.True(CliReadOnlyCommandRunner.TryExecute(args, repository, workspace,
                new InMemoryModelProviderRegistry([]), null, ref agents, ref profiles, ref currentGoal, out var changed));
            Assert.False(changed);
        });
    }

    private static void WithWorkspace(Action<OrchestratorWorkspace> action)
    {
        var root = CreateTempDirectory();
        try { action(OrchestratorWorkspace.ForDirectory(root, executionDirectory: root)); }
        finally
        {
            // Git loose objects are read-only on Windows and must be made writable before deletion.
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, File.GetAttributes(file) & ~FileAttributes.ReadOnly);
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateGitRepository(string root)
    {
        RunGit(root, "init", "-b", "main");
        File.WriteAllText(Path.Combine(root, "seed.txt"), "Provenance fixture");
        RunGit(root, "add", "seed.txt");
        RunGit(root, "commit", "-m", "Seed");
        return RunGit(root, "rev-parse", "HEAD").Trim();
    }

    private static string RunGit(string root, params string[] args)
    {
        var result = GitCli.Run(root,
            ["-c", "user.name=Provenance Tests", "-c", "user.email=provenance@example.invalid",
             "-c", "commit.gpgsign=false", "-c", "core.hooksPath=" + Path.Combine(root, "empty-hooks"), .. args]);
        Assert.True(result.Succeeded, $"git {string.Join(' ', args)}: {result.Error}");
        return result.Output;
    }

    private static SqliteConnection OpenConnection(string path) => new(new SqliteConnectionStringBuilder
    {
        DataSource = path, Mode = SqliteOpenMode.ReadWrite, Pooling = false
    }.ToString());

    private static async Task<byte[]> ReadRowsAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var rows = new List<string?[]>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var values = new string?[reader.FieldCount];
            for (var index = 0; index < values.Length; index++)
                values[index] = reader.IsDBNull(index) ? null :
                    Convert.ToString(reader.GetValue(index), CultureInfo.InvariantCulture);
            rows.Add(values);
        }
        return JsonSerializer.SerializeToUtf8Bytes(rows);
    }

    private static async Task<CliChildProcessResult> RunQueryAsync(string root)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true,
            RedirectStandardError = true, CreateNoWindow = true, WorkingDirectory = root
        };
        start.ArgumentList.Add(typeof(CliArgumentParser).Assembly.Location);
        start.ArgumentList.Add("provenance");
        // Allow only runtime locations; the owned process runner closes stdin and drains both pipes.
        start.Environment.Clear();
        foreach (var name in new[]
        {
            "PATH", "SystemRoot", "WINDIR", "TEMP", "TMP", "USERPROFILE", "LOCALAPPDATA",
            "APPDATA", "ProgramFiles", "ProgramFiles(x86)", "DOTNET_ROOT", "DOTNET_ROOT(x86)"
        })
        {
            if (Environment.GetEnvironmentVariable(name) is { } value)
                start.Environment[name] = value;
        }
        start.Environment["MCG_ORCHESTRATOR_REPOSITORY_ROOT"] = root;
        return await CliChildProcessRunner.RunAsync(start);
    }

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow => now;
    }
}
