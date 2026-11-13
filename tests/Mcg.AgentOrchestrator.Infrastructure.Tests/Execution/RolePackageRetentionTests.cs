using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: every test owns its filesystem root and clock; no shared stores or processes.
public sealed class RolePackageRetentionTests
{
    [Fact]
    public void WriteMirrorsPackageAndDropsStaleFiles()
    {
        using var fixture = new Fixture();
        var task = new TaskSpec(TaskId.New(), "Plan the scoped change.", AgentRole.Planner);
        var goal = new AgentOrchestratorKernel().CreateGoal("Retain context for replay", [task]);
        var context = WorkerContextArtifacts.Write(goal, task, fixture.Worktree,
            citedPriorEvidence: "Recorded prior evidence.", orchestratorStoreRoot: fixture.StoreRoot,
            clock: fixture.Clock, linkedBacklogRecord: "Linked record fixture.");
        var package = Path.Combine(context, "packages", task.Id.Value);
        var durable = RolePackageRetention.TaskDirectory(fixture.StoreRoot, goal.Id, task.Id);

        Assert.True(File.Exists(Path.Combine(package, "linked-backlog-record.md")));
        Assert.True(File.Exists(Path.Combine(package, "prior-goal-evidence.md")));
        Assert.True(File.Exists(Path.Combine(package, "AGENTS.md")));
        AssertMirror(package, durable);
        Assert.False(File.Exists(FailureNote(fixture, goal.Id, task.Id)));

        // Exercise replacement of an existing tree as well as removal of an optional source artifact.
        Directory.CreateDirectory(Path.Combine(durable, "obsolete"));
        File.WriteAllText(Path.Combine(durable, "obsolete", "stale.txt"), "must disappear");
        WorkerContextArtifacts.Write(goal, task, fixture.Worktree,
            orchestratorStoreRoot: fixture.StoreRoot, clock: fixture.Clock);

        Assert.False(File.Exists(Path.Combine(package, "prior-goal-evidence.md")));
        Assert.False(File.Exists(Path.Combine(durable, "prior-goal-evidence.md")));
        Assert.False(Directory.Exists(Path.Combine(durable, "obsolete")));
        AssertMirror(package, durable);
        Assert.False(File.Exists(FailureNote(fixture, goal.Id, task.Id)));
    }

    [Fact]
    public void WriteSurvivesDurableCopyIoFailure()
    {
        using var fixture = new Fixture();
        var task = new TaskSpec(TaskId.New(), "Plan the change.", AgentRole.Planner);
        var goal = new AgentOrchestratorKernel().CreateGoal("Best effort retention", [task]);
        var goalDirectory = RolePackageRetention.GoalDirectory(fixture.StoreRoot, goal.Id);
        Directory.CreateDirectory(goalDirectory);
        var destination = RolePackageRetention.TaskDirectory(fixture.StoreRoot, goal.Id, task.Id);
        File.WriteAllText(destination, "blocks directory move");
        Assert.True(File.Exists(destination));

        var context = WorkerContextArtifacts.Write(goal, task, fixture.Worktree,
            orchestratorStoreRoot: fixture.StoreRoot, clock: fixture.Clock,
            linkedBacklogRecord: "Source remains intact.");

        var package = Path.Combine(context, "packages", task.Id.Value);
        Assert.True(Directory.Exists(package));
        Assert.Equal("Source remains intact.", File.ReadAllText(Path.Combine(package, "linked-backlog-record.md")));
        Assert.Equal(File.ReadAllBytes(Path.Combine(context, "context-package.json")),
            File.ReadAllBytes(Path.Combine(package, "context-package.json")));
        using var metadata = JsonDocument.Parse(File.ReadAllText(Path.Combine(package, "context-package.json")));
        Assert.Equal(task.Id.Value, metadata.RootElement.GetProperty("taskId").GetString());
        var line = Assert.Single(File.ReadAllLines(FailureNote(fixture, goal.Id, task.Id)));
        Assert.Contains(nameof(IOException), line);
        Assert.Contains(task.Id.Value, line);
        Assert.Empty(Directory.GetDirectories(goalDirectory));
        Assert.Equal("blocks directory move", File.ReadAllText(destination));

        File.Delete(destination);
        WorkerContextArtifacts.Write(goal, task, fixture.Worktree,
            orchestratorStoreRoot: fixture.StoreRoot, clock: fixture.Clock);
        AssertMirror(package, destination);
        Assert.False(File.Exists(FailureNote(fixture, goal.Id, task.Id)));
    }

    [Fact]
    public void RetainOverBudgetKeepsNewestAndListsSkipped()
    {
        using var fixture = new Fixture();
        var goalId = GoalId.New();
        var older = TaskId.New();
        var newest = TaskId.New();
        var package = fixture.CreatePackage();
        const long budget = 31; // Each fixture package is 16 bytes; two do not fit.
        RolePackageRetention.Retain(fixture.StoreRoot, goalId, older, package, fixture.Clock, budget);
        Assert.True(Directory.Exists(RolePackageRetention.TaskDirectory(fixture.StoreRoot, goalId, older)));
        fixture.Clock.UtcNow = fixture.Clock.UtcNow.AddDays(-1); // Ordering must survive a clock rollback.
        RolePackageRetention.Retain(fixture.StoreRoot, goalId, newest, package, fixture.Clock, budget);

        var goalDirectory = RolePackageRetention.GoalDirectory(fixture.StoreRoot, goalId);
        var durable = Assert.Single(Directory.GetDirectories(goalDirectory));
        Assert.Equal(newest.Value, Path.GetFileName(durable));
        AssertMirror(package, durable);
        Assert.True(Directory.GetFiles(durable, "*", SearchOption.AllDirectories).Sum(file => new FileInfo(file).Length) <= budget);
        using var manifest = ReadManifest(goalDirectory);
        Assert.Equal(budget, manifest.RootElement.GetProperty("byteBudget").GetInt64());
        Assert.Equal(newest.Value, Assert.Single(manifest.RootElement.GetProperty("retained").EnumerateArray())
            .GetProperty("taskId").GetString());
        Assert.Equal(older.Value, Assert.Single(manifest.RootElement.GetProperty("skipped").EnumerateArray())
            .GetProperty("taskId").GetString());
    }

    [Fact]
    public void RetainSkipsPackageLargerThanBudget()
    {
        using var fixture = new Fixture();
        var goalId = GoalId.New();
        var taskId = TaskId.New();
        var package = fixture.CreatePackage();
        RolePackageRetention.Retain(fixture.StoreRoot, goalId, taskId, package, fixture.Clock, 16);
        Assert.True(Directory.Exists(RolePackageRetention.TaskDirectory(fixture.StoreRoot, goalId, taskId)));
        File.WriteAllBytes(Path.Combine(package, "larger.bin"), new byte[17]);

        RolePackageRetention.Retain(fixture.StoreRoot, goalId, taskId, package, fixture.Clock, 16);

        var goalDirectory = RolePackageRetention.GoalDirectory(fixture.StoreRoot, goalId);
        Assert.Empty(Directory.GetDirectories(goalDirectory));
        using var manifest = ReadManifest(goalDirectory);
        Assert.Empty(manifest.RootElement.GetProperty("retained").EnumerateArray());
        var skipped = Assert.Single(manifest.RootElement.GetProperty("skipped").EnumerateArray());
        Assert.Equal(taskId.Value, skipped.GetProperty("taskId").GetString());
        Assert.Equal("package-exceeds-budget", skipped.GetProperty("reason").GetString());
        Assert.False(File.Exists(FailureNote(fixture, goalId, taskId)));

        File.Delete(Path.Combine(package, "larger.bin"));
        RolePackageRetention.Retain(fixture.StoreRoot, goalId, taskId, package, fixture.Clock, 16);
        using var refreshed = ReadManifest(goalDirectory);
        Assert.Empty(refreshed.RootElement.GetProperty("skipped").EnumerateArray());
        AssertMirror(package, RolePackageRetention.TaskDirectory(fixture.StoreRoot, goalId, taskId));
    }

    [Fact]
    public void RetainRefreshBecomesNewestAndAbsentStoreIsNoOp()
    {
        using var fixture = new Fixture();
        var goalId = GoalId.New();
        var first = TaskId.New();
        var second = TaskId.New();
        var third = TaskId.New();
        var package = fixture.CreatePackage();
        RolePackageRetention.Retain(null, goalId, first, package, fixture.Clock);
        RolePackageRetention.Retain(" ", goalId, first, package, fixture.Clock);
        Assert.False(Directory.Exists(fixture.StoreRoot));

        foreach (var task in new[] { first, second, first, third })
            RolePackageRetention.Retain(fixture.StoreRoot, goalId, task, package, fixture.Clock, 32);

        var goalDirectory = RolePackageRetention.GoalDirectory(fixture.StoreRoot, goalId);
        Assert.Equal(new[] { first.Value, third.Value }.Order(),
            Directory.GetDirectories(goalDirectory).Select(Path.GetFileName).Order());
        using var manifest = ReadManifest(goalDirectory);
        Assert.Equal(second.Value, Assert.Single(manifest.RootElement.GetProperty("skipped").EnumerateArray())
            .GetProperty("taskId").GetString());
    }

    private static string FailureNote(Fixture fixture, GoalId goalId, TaskId taskId) =>
        Path.Combine(RolePackageRetention.GoalDirectory(fixture.StoreRoot, goalId), taskId.Value + RolePackageRetention.FailureNoteSuffix);

    private static JsonDocument ReadManifest(string goalDirectory) =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(goalDirectory, RolePackageRetention.ManifestFileName)));

    private static void AssertMirror(string package, string durable)
    {
        Assert.True(Directory.Exists(durable));
        var expected = Directory.GetFiles(package, "*", SearchOption.AllDirectories)
            .Select(file => Path.GetRelativePath(package, file))
            .Where(path => path != "linked-backlog-record.md").Order().ToArray();
        Assert.NotEmpty(expected);
        Assert.Equal(expected, Directory.GetFiles(durable, "*", SearchOption.AllDirectories)
            .Select(file => Path.GetRelativePath(durable, file)).Order().ToArray());
        foreach (var path in expected)
            Assert.Equal(File.ReadAllBytes(Path.Combine(package, path)), File.ReadAllBytes(Path.Combine(durable, path)));
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = new(2026, 11, 13, 12, 0, 0, TimeSpan.Zero);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = CreateTempDirectory();
        internal string Worktree { get; }
        internal string StoreRoot { get; }
        internal FixedClock Clock { get; } = new();

        internal Fixture()
        {
            Worktree = Path.Combine(_root, "worktree");
            StoreRoot = Path.Combine(_root, "orchestrator");
            Directory.CreateDirectory(Worktree);
            File.WriteAllText(Path.Combine(Worktree, "AGENTS.md"), "Fixture guidance.");
        }

        internal string CreatePackage()
        {
            var package = Path.Combine(_root, "package");
            Directory.CreateDirectory(Path.Combine(package, "guidance"));
            File.WriteAllBytes(Path.Combine(package, "context-package.json"), new byte[8]);
            File.WriteAllBytes(Path.Combine(package, "guidance", "rule.md"), new byte[8]);
            File.WriteAllBytes(Path.Combine(package, "linked-backlog-record.md"), new byte[100]);
            return package;
        }

        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}
