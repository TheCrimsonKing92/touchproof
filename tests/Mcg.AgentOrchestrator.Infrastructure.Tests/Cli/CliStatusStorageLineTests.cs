using System.Text.Json;
using System.Text.Json.Nodes;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: private roots and query repositories; console capture is async-local.
public sealed class CliStatusStorageLineTests : CliTaskQueryTestSupport
{
    private static readonly DateTimeOffset Now = new(2026, 11, 12, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void KnownSnapshot_BothFormsRenderCachedValues(bool withPrefix)
    {
        var root = CreateTempDirectory();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            Directory.CreateDirectory(workspace.OrchestratorDirectory);
            var snapshot = new StorageHealthSnapshot(3221225472, 12, 1, 1, "merge-train-evidence", Now.AddHours(-3));
            File.WriteAllText(SnapshotPath(workspace), snapshot.Serialize());
            File.WriteAllText(Path.Combine(workspace.OrchestratorDirectory, "contradicting-data"), "tiny");
            var before = Files(root);
            var text = "";
            var machine = "";
            var error = AsyncLocalConsoleRouter.CaptureError(() =>
            {
                text = Human(workspace, withPrefix ? ["status", "abcdef12"] : ["status"]);
                machine = Machine(workspace);
            });

            Assert.Empty(error);
            Assert.Equal("storage 3GB, 1 over budget (merge-train-evidence), 1 unregistered, snapshot 3h old",
                Assert.Single(StorageLines(text)));
            Assert.Single(machine.Split('\n', StringSplitOptions.RemoveEmptyEntries));
            using var document = JsonDocument.Parse(machine);
            var storage = document.RootElement.GetProperty("payload").GetProperty("storage");
            Assert.Equal(new[] { "status", "totalBytes", "totalFiles", "overBudgetCount", "largestOverBudgetClass",
                "unregisteredCount", "measuredAtUtc", "ageSeconds" }, storage.EnumerateObject().Select(p => p.Name));
            Assert.Equal("ok", storage.GetProperty("status").GetString());
            Assert.Equal(3221225472, storage.GetProperty("totalBytes").GetInt64());
            Assert.Equal(12, storage.GetProperty("totalFiles").GetInt64());
            Assert.Equal(1, storage.GetProperty("overBudgetCount").GetInt32());
            Assert.Equal("merge-train-evidence", storage.GetProperty("largestOverBudgetClass").GetString());
            Assert.Equal(1, storage.GetProperty("unregisteredCount").GetInt32());
            Assert.Equal(Now.AddHours(-3), storage.GetProperty("measuredAtUtc").GetDateTimeOffset());
            Assert.Equal(10800, storage.GetProperty("ageSeconds").GetInt64());
            Assert.Equal(before, Files(root));
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("missing-directory")]
    [InlineData("missing-file")]
    [InlineData("empty")]
    [InlineData("malformed")]
    [InlineData("array")]
    [InlineData("missing-field")]
    [InlineData("negative-bytes")]
    [InlineData("negative-files")]
    [InlineData("negative-over")]
    [InlineData("negative-unregistered")]
    [InlineData("fraction")]
    [InlineData("overflow")]
    [InlineData("null-offender")]
    [InlineData("empty-offender")]
    [InlineData("multiline-offender")]
    [InlineData("bad-time")]
    [InlineData("wrong-time-kind")]
    [InlineData("oversized")]
    [InlineData("locked")]
    [InlineData("directory")]
    public void UnavailableSnapshot_BothFormsRenderUnknownWithoutWrites(string scenario)
    {
        var root = CreateTempDirectory();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            if (scenario != "missing-directory") Directory.CreateDirectory(workspace.OrchestratorDirectory);
            var path = SnapshotPath(workspace);
            if (scenario == "directory") Directory.CreateDirectory(path);
            else if (scenario is not ("missing-directory" or "missing-file"))
                File.WriteAllText(path, InvalidSnapshot(scenario));
            var before = Files(root);
            var directories = Directory.GetDirectories(root, "*", SearchOption.AllDirectories).Order().ToArray();
            using var held = scenario == "locked" ? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None) : null;
            var text = "";
            var machine = "";
            var error = AsyncLocalConsoleRouter.CaptureError(() =>
            {
                text = Human(workspace, ["status", "abcdef12"]);
                machine = Machine(workspace);
            });

            Assert.Empty(error);
            Assert.Equal("storage unknown", Assert.Single(StorageLines(text)));
            using var document = JsonDocument.Parse(machine);
            var storage = document.RootElement.GetProperty("payload").GetProperty("storage");
            Assert.Equal("status", Assert.Single(storage.EnumerateObject()).Name);
            Assert.Equal("unknown", storage.GetProperty("status").GetString());
            held?.Dispose();
            Assert.Equal(before, Files(root));
            Assert.Equal(directories, Directory.GetDirectories(root, "*", SearchOption.AllDirectories).Order().ToArray());
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(-100, 0, "0s")]
    [InlineData(90, 90, "1m")]
    [InlineData(259200, 259200, "3d")]
    public void ValidSnapshot_AgeIsClampedAndOldMeasurementsRemainVisible(int age, long seconds, string display)
    {
        var root = CreateTempDirectory();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            Directory.CreateDirectory(workspace.OrchestratorDirectory);
            File.WriteAllText(SnapshotPath(workspace), new StorageHealthSnapshot(0, 0, 0, 0, null,
                Now.AddSeconds(-age)).Serialize());
            Assert.Equal($"storage 0B, 0 over budget, 0 unregistered, snapshot {display} old",
                Assert.Single(StorageLines(Human(workspace, ["status", "abcdef12"]))));
            using var document = JsonDocument.Parse(Machine(workspace));
            var storage = document.RootElement.GetProperty("payload").GetProperty("storage");
            Assert.Equal("ok", storage.GetProperty("status").GetString());
            Assert.Equal(JsonValueKind.Null, storage.GetProperty("largestOverBudgetClass").ValueKind);
            Assert.Equal(seconds, storage.GetProperty("ageSeconds").GetInt64());
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("status", "--tasks-only")]
    [InlineData("monitor", "abcdef12")]
    public void OtherHumanForms_KeepTheirExistingOutput(string verb, string option)
    {
        var root = CreateTempDirectory();
        try
        {
            Assert.Empty(StorageLines(Human(OrchestratorWorkspace.ForDirectory(root), [verb, option])));
        }
        finally { Directory.Delete(root, true); }
    }

    private static string InvalidSnapshot(string scenario)
    {
        var json = JsonNode.Parse(new StorageHealthSnapshot(100, 2, 1, 1, "logs", Now).Serialize())!.AsObject();
        switch (scenario)
        {
            case "empty": return "";
            case "malformed": return "{";
            case "array": return "[]";
            case "oversized": return new string(' ', 65537);
            case "missing-field": json.Remove("totalFiles"); break;
            case "negative-bytes": json["totalBytes"] = -1; break;
            case "negative-files": json["totalFiles"] = -1; break;
            case "negative-over": json["overBudgetCount"] = -1; break;
            case "negative-unregistered": json["unregisteredCount"] = -1; break;
            case "fraction": json["totalFiles"] = 1.5; break;
            case "overflow": json["overBudgetCount"] = long.MaxValue; break;
            case "null-offender": json["largestOverBudgetClass"] = null; break;
            case "empty-offender": json["largestOverBudgetClass"] = " "; break;
            case "multiline-offender": json["largestOverBudgetClass"] = "logs\nextra"; break;
            case "bad-time": json["measuredAtUtc"] = "not a time"; break;
            case "wrong-time-kind": json["measuredAtUtc"] = 42; break;
        }
        return json.ToJsonString();
    }

    private static string[] Files(string root) => Directory.GetFiles(root, "*", SearchOption.AllDirectories)
        .Order().Select(path => Path.GetRelativePath(root, path) + ":" + Convert.ToHexString(File.ReadAllBytes(path))).ToArray();

    private static IEnumerable<string> StorageLines(string text) =>
        text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Where(line => line.StartsWith("storage "));

    private static string SnapshotPath(OrchestratorWorkspace workspace) =>
        Path.Combine(workspace.OrchestratorDirectory, StorageHealthSnapshotWriter.FileName);

    private static AgentOrchestratorKernel Kernel()
    {
        var kernel = new AgentOrchestratorKernel();
        kernel.CreateGoal(new GoalId("abcdef12abcdef12abcdef12abcdef12"), "Storage fixture", []);
        return kernel;
    }

    private static string Machine(OrchestratorWorkspace workspace) => CaptureConsole(() =>
        CliStatusJsonQueryCommand.Execute(["status", "abcdef12", "--json"],
            new QueryOnlyStateRepository(Kernel()), workspace, new FixedTimeProvider()));

    private static string Human(OrchestratorWorkspace workspace, string[] args)
    {
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var profiles = WorkerProfileCatalog.Default();
        Goal? current = null;
        return CaptureConsole(() => CliStatusQueryCommand.Execute(args, new QueryOnlyStateRepository(Kernel()),
            workspace, new InMemoryModelProviderRegistry([]), ref agents, ref profiles, ref current, new FixedTimeProvider()));
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
