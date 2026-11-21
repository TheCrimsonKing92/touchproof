using System.Security.Cryptography;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each test owns its stores and captures console output in its async context.
public sealed class ContextReworkUsageNoStoreWriteTests
{
    [Fact]
    public async Task ReportPreservesStoresWhileReadingLiveAndArchivedRetries()
    {
        var root = SharedTestSupport.CreateTempDirectory();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            var live = CreateGoal("aa123456aa123456aa123456aa123456", "live-task", GoalStatus.Active, 100);
            var archived = CreateGoal("bb123456bb123456bb123456bb123456", "archived-task", GoalStatus.Completed, 200);
            var kernel = AgentOrchestratorKernel.FromSnapshot(new OrchestratorSnapshot([live, archived], []));
            StateDbMigrations.EnsureUpToDate(workspace.SqliteStatePath);
            var repository = new SqliteOrchestratorStateRepository(workspace.SqliteStatePath);
            await repository.SaveAsync(kernel);
            Assert.Equal(1, await repository.ArchiveTerminalGoalSnapshotsAsync(
                ContextReworkUsageUnclassifiedTests.RetryAt.AddDays(1)));

            var loaded = await repository.LoadAsync();
            Assert.False(loaded.GetGoal(new GoalId(live.Id)).IsMetadataOnly);
            var stub = loaded.GetGoal(new GoalId(archived.Id));
            Assert.True(stub.IsMetadataOnly);
            Assert.Empty(stub.Tasks);

            const string sentinel = "other-store.db";
            File.WriteAllText(Path.Combine(root, sentinel), "unrelated store sentinel");
            var statePath = Path.GetRelativePath(root, workspace.SqliteStatePath);
            var before = Fingerprint(root);
            Assert.Contains(statePath, before.Keys);
            Assert.Contains(sentinel, before.Keys);

            using var error = new StringWriter();
            var exit = -1;
            var output = AsyncLocalConsoleRouter.Capture(() => AsyncLocalConsoleRouter.Error.CaptureLocal(
                () => exit = CliContextUsageCommand.Run(["context-usage", "--rework", "--json"], workspace), error));

            Assert.True(exit == 0, error.ToString());
            using var report = JsonDocument.Parse(output);
            var coverage = report.RootElement.GetProperty("coverage");
            Assert.Equal(1, coverage.GetProperty("live").GetInt32());
            Assert.Equal(1, coverage.GetProperty("archivedHydrated").GetInt32());
            Assert.Equal(0, coverage.GetProperty("archivedUnavailable").GetInt32());
            var row = Assert.Single(report.RootElement.GetProperty("rows").EnumerateArray());
            Assert.Equal(2, row.GetProperty("attempts").GetInt32());
            Assert.Equal(300L, row.GetProperty("input").GetInt64());
            Assert.Collection(row.GetProperty("examples").EnumerateArray(),
                pair => AssertExample(pair, live), pair => AssertExample(pair, archived));
            Assert.Empty(ChangedFiles(root, before));

            // Negative control: the same comparison detects a committed write to the fixture database.
            loaded.CreateGoal("Negative-control store write");
            await repository.SaveAsync(loaded);
            Assert.Contains(statePath, ChangedFiles(root, before));
        }
        finally
        {
            SharedTestSupport.RemoveTempDirectory(root);
        }
    }

    private static GoalSnapshot CreateGoal(string id, string taskId, GoalStatus status, long input)
    {
        var task = ContextReworkUsageUnclassifiedTests.CreateRetriedTask(taskId, input, RetryCause.NewSourceFinding, []);
        return new GoalSnapshot(id, taskId, status, [task],
            [new(id, taskId, ProgressKind.TaskCompleted, "Fixture task completed",
                ContextReworkUsageUnclassifiedTests.RetryAt.AddMinutes(1))]);
    }

    private static void AssertExample(JsonElement pair, GoalSnapshot goal)
    {
        Assert.Equal(goal.Id, pair.GetProperty("goalId").GetString());
        Assert.Equal(Assert.Single(goal.Tasks).Id, pair.GetProperty("taskId").GetString());
    }

    private static Dictionary<string, string> Fingerprint(string root) =>
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).ToDictionary(
            path => Path.GetRelativePath(root, path),
            path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))),
            StringComparer.OrdinalIgnoreCase);

    private static IEnumerable<string> ChangedFiles(string root, IReadOnlyDictionary<string, string> before)
    {
        var after = Fingerprint(root);
        foreach (var (path, hash) in before)
        {
            if (!after.TryGetValue(path, out var current) || current != hash)
                yield return path;
        }

        foreach (var path in after.Keys.Except(before.Keys, StringComparer.OrdinalIgnoreCase))
        {
            var isReadSidecar = (path.EndsWith(".db-shm", StringComparison.Ordinal) ||
                                 (path.EndsWith(".db-wal", StringComparison.Ordinal) &&
                                  new FileInfo(Path.Combine(root, path)).Length == 0)) &&
                                before.ContainsKey(path[..^4]);
            if (!isReadSidecar)
                yield return path;
        }
    }
}
