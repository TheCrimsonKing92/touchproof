using System.Security.Cryptography;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

// Parallel-safe: the subprocess and SQLite fixture use this test's own directories.
public sealed class DecisionSnapshotCliTests : CliCommandTestBase
{
    private const string FixedTime = "2026-11-20T12:00:00Z";
    private static readonly GoalId FirstId = new("10000000000000000000000000000000");
    private static readonly GoalId WalId = new("20000000000000000000000000000000");

    private protected override bool UsesGit => false;

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CommandReadsEveryGoalAndPreservesEveryCopiedFile(bool withWal, bool withShm)
    {
        using var fixture = new DecisionSnapshotTestDirectory();
        var original = Directory.CreateDirectory(Path.Combine(fixture.Path, "original")).FullName;
        var copy = Directory.CreateDirectory(Path.Combine(fixture.Path, "copied state")).FullName;
        var db = Path.Combine(original, "state.db");
        StateDbMigrations.EnsureUpToDate(db);
        var kernel = new AgentOrchestratorKernel();
        kernel.CreateGoal(FirstId, "Stored goal in the main database");
        var repository = new SqliteOrchestratorStateRepository(db);
        await repository.SaveAsync(kernel);
        var copiedDb = Path.Combine(copy, "state.db");

        using (var keeper = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = db, Pooling = false }.ToString()))
        {
            if (withWal)
            {
                keeper.Open();
                using var checkpoint = keeper.CreateCommand();
                checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";
                using (var row = checkpoint.ExecuteReader())
                {
                    Assert.True(row.Read());
                    Assert.Equal(0L, row.GetInt64(0));
                }
                var mainBefore = SHA256.HashData(File.ReadAllBytes(db));
                var completed = new GoalSnapshot(WalId.Value, "Cancelled goal committed only to WAL", GoalStatus.Cancelled, [], []);
                await repository.SaveAsync(AgentOrchestratorKernel.FromSnapshot(
                    kernel.ExportSnapshot() with { Goals = [.. kernel.ExportSnapshot().Goals, completed] }));
                Assert.Equal(mainBefore, SHA256.HashData(File.ReadAllBytes(db)));
                Assert.True(new FileInfo(db + "-wal").Length > 32, "WAL must contain frames before the copy.");
                File.Copy(db + "-wal", copiedDb + "-wal");
                if (withShm) File.Copy(db + "-shm", copiedDb + "-shm");
            }

            File.Copy(db, copiedDb);
        }

        Directory.CreateDirectory(Path.Combine(copy, "unrelated"));
        File.WriteAllText(Path.Combine(copy, "unrelated", "sentinel.txt"), "copy contents");
        var before = Inventory(copy);
        Assert.Equal(withWal, before.ContainsKey("state.db-wal"));
        Assert.Equal(withShm, before.ContainsKey("state.db-shm"));
        var output = Path.Combine(fixture.Path, "decisions.json");

        var result = RunAppCommand(fixture.Path, "decision-snapshot",
            "--state-directory", copy, "--output", output, "--as-of", FixedTime);

        Assert.True(result.ExitCode == 0, result.Stderr);
        var snapshot = DecisionSnapshot.Read(output);
        Assert.Equal(new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.Zero), snapshot.EvaluationContext.AsOf);
        Assert.Equal(0, snapshot.EvaluationContext.SlotIndex);
        Assert.Equal("Auto", snapshot.EvaluationContext.VerifiedTransition);
        Assert.Equal(withWal ? new[] { FirstId.Value, WalId.Value } : new[] { FirstId.Value },
            snapshot.Goals.Select(goal => goal.GoalId));
        Assert.All(snapshot.Goals, goal =>
        {
            Assert.Null(goal.Exception);
            Assert.Equal(ConductorDriver.CandidateDeclineLifecycleNotVerified, goal.Decision!.DeclineReason);
        });
        if (withWal) Assert.Equal(GoalStatus.Cancelled, snapshot.Goals[1].Decision!.Status);
        Assert.Equal(before.ToArray(), Inventory(copy).ToArray());
    }

    [Fact]
    public async Task CommandRejectsAnOutputInsideTheCopyAndCleansUpFailedPublication()
    {
        using var fixture = new DecisionSnapshotTestDirectory();
        var copy = Directory.CreateDirectory(Path.Combine(fixture.Path, "copy")).FullName;
        var outputDirectory = Directory.CreateDirectory(Path.Combine(fixture.Path, "existing-dir")).FullName;
        var db = Path.Combine(copy, "state.db");
        StateDbMigrations.EnsureUpToDate(db);
        await new SqliteOrchestratorStateRepository(db).SaveAsync(new AgentOrchestratorKernel());
        var before = Inventory(copy);
        var beforeRoot = Inventory(fixture.Path);

        var nested = RunAppCommand(fixture.Path, "decision-snapshot",
            "--state-directory", copy, "--output", Path.Combine(copy, "decisions.json"));
        Assert.NotEqual(0, nested.ExitCode);
        Assert.Contains("outside the state directory", nested.Stderr, StringComparison.Ordinal);
        Assert.Equal(before.ToArray(), Inventory(copy).ToArray());

        var cannotPublish = RunAppCommand(fixture.Path, "decision-snapshot",
            "--state-directory", copy, "--output", outputDirectory);
        Assert.NotEqual(0, cannotPublish.ExitCode);
        Assert.Contains("Decision snapshot failed", cannotPublish.Stderr, StringComparison.Ordinal);
        Assert.True(Directory.Exists(outputDirectory));
        Assert.Equal(beforeRoot.ToArray(), Inventory(fixture.Path).ToArray());
    }

    private static SortedDictionary<string, string> Inventory(string root)
    {
        var files = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            files.Add(Path.GetRelativePath(root, path), Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
        }
        return files;
    }
}
