using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// The retention-test hypothesis for the earlier temp-root losses remains unconfirmed.
// The non-parallel collection isolates this test's process-wide environment mutation.
[Xunit.Collection(TestCollections.EnvMutation)]
public sealed class StorageRetentionExplicitMtpRootNegativeControlTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-08-20T12:00:00Z");
    private const string GoalId = "11111111111111111111111111111111";

    [Xunit.Fact]
    public void WorkspaceRun_ExplicitMtpRoot_LeavesLocalAppDataSentinelTreeUntouched()
    {
        var root = Path.Combine(Path.GetTempPath(), "mcg-retention-explicit-root", Guid.NewGuid().ToString("n"));
        var priorLocalAppData = Environment.GetEnvironmentVariable("LOCALAPPDATA");
        try
        {
            var localAppData = Path.Combine(root, "sentinel-profile");
            var sentinelRoot = Path.Combine(localAppData, "Temp", "Low", "mcg-tests");
            var fixtureRoot = Path.Combine(root, "fixture-mtp-results");
            var workspace = OrchestratorWorkspace.ForDirectory(Path.Combine(root, "workspace"));
            var goalDirectory = Path.Combine(workspace.OrchestratorDirectory, "acceptance-gate-attempts", GoalId);
            Directory.CreateDirectory(goalDirectory);
            for (var ordinal = 1; ordinal <= StorageRetentionMaintenance.MtpResultMaxRetainedDirectories + 2; ordinal++)
            {
                var attemptId = $"mtp-{ordinal:D3}";
                File.WriteAllText(Path.Combine(goalDirectory, attemptId + ".attempt.json"), JsonSerializer.Serialize(new
                {
                    attemptId,
                    goalId = GoalId,
                    ordinal,
                    startedAt = Now.AddMinutes(ordinal),
                    outcome = 1,
                    reconciledAt = Now
                }));
                // The final attempt has no run, leaving every run eligible for the count bound.
                if (ordinal <= StorageRetentionMaintenance.MtpResultMaxRetainedDirectories + 1)
                {
                    WriteMtpRun(sentinelRoot, attemptId, Now.AddMinutes(-ordinal));
                    WriteMtpRun(fixtureRoot, attemptId, Now.AddMinutes(-ordinal));
                }
            }

            var processRoot = Path.Combine(sentinelRoot, $"p{Environment.ProcessId:x}");
            Directory.CreateDirectory(Path.Combine(processRoot, "empty"));
            File.WriteAllBytes(Path.Combine(processRoot, "payload.bin"), [0, 1, 127, 128, 255]);
            var before = SnapshotTree(sentinelRoot);
            var kernel = new AgentOrchestratorKernel();
            var goal = kernel.CreateGoal(new GoalId(GoalId), "explicit MTP root fixture",
                [new TaskSpec(new TaskId("22222222222222222222222222222222"), "fixture task", AgentRole.Developer)]);
            goal = kernel.CancelGoal(goal.Id, "fixture terminal state");

            StorageRetentionResult result;
            try
            {
                Environment.SetEnvironmentVariable("LOCALAPPDATA", localAppData);
                Assert.Equal(sentinelRoot, StorageRetentionMaintenance.DefaultMtpResultsRoot());
                result = StorageRetentionMaintenance.Run(workspace, [goal], Now, mtpResultsRoot: fixtureRoot);
            }
            finally
            {
                Environment.SetEnvironmentVariable("LOCALAPPDATA", priorLocalAppData);
            }

            Assert.Equal(priorLocalAppData, Environment.GetEnvironmentVariable("LOCALAPPDATA"));
            var after = SnapshotTree(sentinelRoot);
            Assert.Equal(before.Directories, after.Directories);
            Assert.Equal(before.Files.Keys.ToArray(), after.Files.Keys.ToArray());
            foreach (var (path, bytes) in before.Files)
            {
                Assert.Equal(bytes, after.Files[path]);
            }

            var expectedSurvivors = Enumerable.Range(1, StorageRetentionMaintenance.MtpResultMaxRetainedDirectories)
                .Select(ordinal => Path.Combine(fixtureRoot, $"retained-mtp-{ordinal:D3}"))
                .OrderBy(path => path, StringComparer.Ordinal).ToArray();
            Assert.Equal(expectedSurvivors,
                Directory.GetDirectories(fixtureRoot).OrderBy(path => path, StringComparer.Ordinal).ToArray());
            var deletedRun = Path.Combine(fixtureRoot,
                $"retained-mtp-{StorageRetentionMaintenance.MtpResultMaxRetainedDirectories + 1:D3}");
            Assert.False(Directory.Exists(deletedRun));
            Assert.Contains(result.Decisions, decision =>
                decision.Family == EvidenceArtifactFamily.MtpTestRuns &&
                decision.Action == EvidenceRetentionAction.Deleted &&
                decision.Reason == "past-count-bound" &&
                decision.Path == deletedRun);
            Assert.DoesNotContain(result.Decisions, decision =>
                decision.Path.Equals(sentinelRoot, StringComparison.OrdinalIgnoreCase) ||
                decision.Path.StartsWith(sentinelRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Environment.SetEnvironmentVariable("LOCALAPPDATA", priorLocalAppData);
            try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static void WriteMtpRun(string root, string attemptId, DateTimeOffset createdAt)
    {
        var directory = Path.Combine(root, $"retained-{attemptId}");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, ".mtp-run-ownership.json"), JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            attemptId,
            machineName = Environment.MachineName,
            ownerProcessId = 0,
            createdAt,
            runLabel = "explicit-root-fixture"
        }));
        File.WriteAllBytes(Path.Combine(directory, "payload.bin"), [0, 1, 127, 128, 255]);
    }

    private static (string[] Directories, SortedDictionary<string, byte[]> Files) SnapshotTree(string root)
    {
        var directories = Directory.GetDirectories(root, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(root, path)).OrderBy(path => path, StringComparer.Ordinal).ToArray();
        var files = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            files.Add(Path.GetRelativePath(root, path), File.ReadAllBytes(path));
        }
        return (directories, files);
    }
}
