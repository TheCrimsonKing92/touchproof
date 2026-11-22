using System.Text.Json.Nodes;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

// Every filesystem root, registry and SQLite store belongs to this fixture.
public sealed class BuildRootReapCadenceReclaimScenarioTests : IDisposable
{
    private readonly string fixture = Directory.CreateTempSubdirectory("build-root-cadence-scenario-").FullName;
    private readonly TerminalGoalSweep.OwnedRootSweepState state = new();
    private readonly TerminalGoalSweepBuildRootCadenceTests.SweepClock clock = new();
    private string Repo => Path.Combine(fixture, "repo");
    private string Db => Path.Combine(Repo, ".orchestrator", "state.db");
    private DotnetBuildStorageRoot Storage => new(Path.Combine(fixture, "storage"));

    [Fact]
    public void Sweep_reclaims_at_the_interval_and_terminal_trigger_while_protecting_live_roots()
    {
        StateDbMigrations.EnsureUpToDate(Db);
        var projects = new OrchestratorProjectRegistry(Path.Combine(fixture, "registry"), Path.Combine(fixture, "data"));
        var policy = ConductorAutonomyPolicy.Conservative with { BuildRootRetentionDays = 3 };
        var initiallyTerminal = Enumerable.Range(1, 3).Select(index => Seed(index, GoalStatus.Completed)).ToArray();
        var transitioning = Seed(4, GoalStatus.Active);
        var active = Seed(5, GoalStatus.Active);
        var parked = Seed(6, GoalStatus.Parked);
        var activeSnapshot = TerminalBuildRootRetentionScenarioTests.Snapshot(active.Root);
        var parkedSnapshot = TerminalBuildRootRetentionScenarioTests.Snapshot(parked.Root);

        TerminalGoalSweepOwnedRootResult Sweep(bool transitioned = false) =>
            TerminalGoalSweep.ReapOwnedBuildRoots(Db, reclaimGoalRoots: false,
                tempRoot: Path.Combine(fixture, "temp"), clock: clock, sweepState: state,
                storageRoot: Storage, orchestratorTempParent: Path.Combine(fixture, "orchestrator-temp"),
                goals: [(transitioning.Id, transitioned), (active.Id, false), (parked.Id, false)],
                reapCore: () => TerminalGoalSweep.ReapOwnedBuildRootsCore(Db, Storage, state,
                    _ => false, usesSharedStorageRoot: true, canonicalRepoRoot: Repo, projectRegistry: projects,
                    reclaimGoalRoots: false, policy: policy, timeProvider: clock, useIncrementalRetainedBytes: true));

        void AssertProtected()
        {
            Assert.Equal(activeSnapshot, TerminalBuildRootRetentionScenarioTests.Snapshot(active.Root));
            Assert.Equal(parkedSnapshot, TerminalBuildRootRetentionScenarioTests.Snapshot(parked.Root));
        }

        Assert.Contains("reclaimed=2", Sweep().BuildRootRetentionSummary);
        Assert.All(initiallyTerminal.Take(2), goal => Assert.False(Directory.Exists(goal.Root)));
        Assert.True(Directory.Exists(initiallyTerminal[2].Root));
        Assert.True(Directory.Exists(transitioning.Root));
        AssertProtected();

        clock.Advance(TimeSpan.FromMinutes(1));
        var skipped = Sweep();
        Assert.Null(skipped.BuildRootRetentionSummary);
        Assert.Equal(0, skipped.RemovedCount);
        Assert.True(Directory.Exists(initiallyTerminal[2].Root));
        AssertProtected();

        clock.Advance(TimeSpan.FromMinutes(4));
        Assert.Contains("reclaimed=1", Sweep().BuildRootRetentionSummary);
        Assert.False(Directory.Exists(initiallyTerminal[2].Root));
        Assert.True(Directory.Exists(transitioning.Root));
        AssertProtected();

        MarkCompleted(transitioning.Id);
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Contains("reclaimed=1", Sweep(transitioned: true).BuildRootRetentionSummary);
        Assert.False(Directory.Exists(transitioning.Root));
        AssertProtected();

        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Null(Sweep(transitioned: true).BuildRootRetentionSummary);
        AssertProtected();
    }

    private (GoalId Id, string Root) Seed(int number, GoalStatus status)
    {
        var id = new GoalId($"{number:00000000}000000000000000000000000");
        var root = DotnetBuildEnvironmentManager.CreateAttempt(id, "acceptance",
            storageRoot: Storage, repositoryRoot: Repo).RootPath;
        File.WriteAllBytes(Path.Combine(root, "payload.bin"), new byte[1000]);
        var leasePath = Path.Combine(root, "lease", "lease.json");
        var lease = JsonNode.Parse(File.ReadAllText(leasePath))!;
        lease["lastUsedAt"] = clock.GetUtcNow().AddDays(-10);
        File.WriteAllText(leasePath, lease.ToJsonString());
        TerminalBuildRootRetentionScenarioTests.InsertGoal(Db, id.Value, status.ToString(), clock.GetUtcNow().AddDays(-10));
        return (id, root);
    }

    private void MarkCompleted(GoalId id)
    {
        using var connection = new SqliteConnection($"Data Source={Db};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE goals SET status = 'Completed' WHERE id = $id";
        command.Parameters.AddWithValue("$id", id.Value);
        Assert.Equal(1, command.ExecuteNonQuery());
    }

    public void Dispose()
    {
        state.RetainedBytes.Dispose();
        while (DotnetBuildEnvironmentManager.DrainOwnedRunRootWriteFailures(25, Storage).Count == 25) { }
        Directory.Delete(fixture, recursive: true);
    }
}
