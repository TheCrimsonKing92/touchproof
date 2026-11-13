using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel safe: each test owns a private temporary root; console capture is AsyncLocal.
public sealed class CliStorageHealthHeldEvidenceTests : CliTaskQueryTestSupport
{
    [Fact]
    public void SnapshotRows_TextAndJsonExposeHeldEvidenceInOrder()
    {
        var root = CreateTempDirectory();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            Directory.CreateDirectory(workspace.OrchestratorDirectory);
            File.WriteAllBytes(Path.Combine(workspace.OrchestratorDirectory, "unregistered-fixture"), [1]);
            AcceptanceHeldEvidenceRow[] rows =
            [
                new("DeferredLive", "attempt-is-unreconciled", 2, 7, 1),
                new("Preserved", "final-attempt", 1, 11, 0)
            ];
            var snapshotPath = Path.Combine(workspace.OrchestratorDirectory, StorageHealthSnapshotWriter.FileName);
            File.WriteAllText(snapshotPath, new StorageHealthSnapshot(18, 3, 0, 1, null,
                DateTimeOffset.Parse("2026-09-20T12:00:00Z"), rows).Serialize());
            var before = Snapshot(workspace.OrchestratorDirectory);

            var text = Run(["storage-health"], workspace);
            var lines = text.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
            var held = lines.Where(line => line.StartsWith("held\t", StringComparison.Ordinal)).ToArray();
            Assert.Equal(new[]
            {
                "held\taction=DeferredLive\treason=attempt-is-unreconciled\tcount=2\tbytes=7\tunmeasured=1",
                "held\taction=Preserved\treason=final-attempt\tcount=1\tbytes=11"
            }, held);
            var lastUnregistered = Array.FindLastIndex(lines, line => line.StartsWith("unregistered\t", StringComparison.Ordinal));
            Assert.True(lastUnregistered >= 0);
            Assert.Equal(held[0], lines[lastUnregistered + 1]);
            Assert.StartsWith("totals\t", lines[lastUnregistered + 3]);

            using var document = JsonDocument.Parse(Run(["storage-health", "--json"], workspace));
            var jsonRows = document.RootElement.GetProperty("held").EnumerateArray().ToArray();
            Assert.Equal(rows.Length, jsonRows.Length);
            foreach (var (expected, actual) in rows.Zip(jsonRows))
            {
                Assert.Equal(expected.Action, actual.GetProperty("action").GetString());
                Assert.Equal(expected.Reason, actual.GetProperty("reason").GetString());
                Assert.Equal(expected.DecisionCount, actual.GetProperty("count").GetInt32());
                Assert.Equal(expected.Bytes, actual.GetProperty("bytes").GetInt64());
                Assert.Equal(expected.Unmeasured, actual.GetProperty("unmeasured").GetInt32());
            }
            Assert.True(document.RootElement.TryGetProperty("classes", out _));
            Assert.True(document.RootElement.TryGetProperty("unregistered", out _));
            Assert.True(document.RootElement.TryGetProperty("totals", out _));
            Assert.Equal(before, Snapshot(workspace.OrchestratorDirectory));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingOrLegacySnapshot_PrintsTotalsAndEmptyHeldWithoutWrites(bool legacy)
    {
        var root = CreateTempDirectory();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            if (legacy)
            {
                Directory.CreateDirectory(workspace.OrchestratorDirectory);
                File.WriteAllText(Path.Combine(workspace.OrchestratorDirectory, StorageHealthSnapshotWriter.FileName), """
                    {"totalBytes":0,"totalFiles":0,"overBudgetCount":0,"unregisteredCount":0,
                     "largestOverBudgetClass":null,"measuredAtUtc":"2026-09-20T12:00:00Z"}
                    """);
            }
            var before = Snapshot(workspace.OrchestratorDirectory);

            var text = Run(["storage-health"], workspace);
            Assert.DoesNotContain("held\t", text);
            Assert.Contains("totals\tbytes=", text);
            using var document = JsonDocument.Parse(Run(["storage-health", "--json"], workspace));
            Assert.Empty(document.RootElement.GetProperty("held").EnumerateArray());
            Assert.Equal(before, Snapshot(workspace.OrchestratorDirectory));
            Assert.Equal(legacy, Directory.Exists(workspace.OrchestratorDirectory));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static string[] Snapshot(string directory) => Directory.Exists(directory)
        ? Directory.GetFiles(directory, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
            .Select(path => path + ":" + Convert.ToHexString(File.ReadAllBytes(path))).ToArray()
        : [];

    private static string Run(string[] args, OrchestratorWorkspace workspace)
    {
        var repository = new ProbeStateRepository(new AgentOrchestratorKernel()) { ThrowOnOutbox = true };
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var profiles = WorkerProfileCatalog.Default();
        Goal? goal = null;
        var output = "";
        var error = AsyncLocalConsoleRouter.CaptureError(() => output = CaptureConsole(() =>
        {
            Assert.True(CliReadOnlyCommandRunner.TryExecute(args, repository, workspace,
                new InMemoryModelProviderRegistry([]), ref agents, ref profiles, ref goal, out var changed));
            Assert.False(changed);
        }));
        Assert.Equal("", error);
        Assert.Null(goal);
        Assert.Empty(repository.ObservedWriteOperationTags);
        Assert.Equal(0, repository.FullLoadAttempts + repository.LoadGoalsCount + repository.LoadGoalCount +
            repository.ListGoalMetadataCount + repository.SaveAttempts + repository.MergeSaveAttempts +
            repository.MutationAttempts + repository.ListOutboxMessagesCount + repository.OutboxClaimAttempts);
        return output;
    }
}
